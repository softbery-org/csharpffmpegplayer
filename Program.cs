using FFmpeg.AutoGen;
using System.Runtime.InteropServices;
using SDL2;

namespace CSharpFFmpeg;

class Program
{
    static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        bool useGpu = args.Contains("-gpu") || args.Contains("--gpu");
        int targetFps = 0;
        string? subtitlePath = null;
        string? playlistPath = null;
        string? urlListPath = null;
        var mediaFiles = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--fps" || args[i] == "-fps") && i + 1 < args.Length)
            {
                int.TryParse(args[i + 1], out targetFps);
                i++;
            }
            else if ((args[i] == "--subtitle" || args[i] == "-sub") && i + 1 < args.Length)
            {
                subtitlePath = args[i + 1];
                i++;
            }
            else if ((args[i] == "--playlist" || args[i] == "-pl") && i + 1 < args.Length)
            {
                playlistPath = args[i + 1];
                i++;
            }
            else if ((args[i] == "--url-list" || args[i] == "-ul") && i + 1 < args.Length)
            {
                urlListPath = args[i + 1];
                i++;
            }
            else if (args[i] == "-gpu" || args[i] == "--gpu" || args[i] == "--help" || args[i] == "-h" ||
                     args[i] == "--url-list" || args[i] == "-ul")
            {
                continue;
            }
            else if (int.TryParse(args[i], out _))
            {
                continue;
            }
            else
            {
                // Collect media files (first one or any path that's not a flag value)
                string arg = args[i];
                if (arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    mediaFiles.Add(arg);
                }
                else if (File.Exists(arg) || Directory.Exists(arg))
                    mediaFiles.Add(arg);
            }
        }

        string? libPath = null;
        // Detect lib path: a path that exists as directory and contains FFmpeg libs
        foreach (var arg in args)
        {
            if (arg == "-gpu" || arg == "--gpu" || arg == "--fps" || arg == "-fps" || arg == "--subtitle" || arg == "-sub" || arg == "--playlist" || arg == "-pl" || arg == "--help" || arg == "-h")
                continue;
            if (int.TryParse(arg, out _))
                continue;
            if (Directory.Exists(arg) && Directory.GetFiles(arg, "libavformat.so*").Length > 0)
            {
                libPath = arg;
                break;
            }
        }

        // Determine the bundled lib directory
        string appDir = AppContext.BaseDirectory;
        string bundledLibDir = Path.Combine(appDir, "lib");

        // If user specified a lib path, use it; otherwise prefer bundled, then system
        if (libPath != null)
        {
            ffmpeg.RootPath = libPath;
        }
        else if (Directory.Exists(bundledLibDir) && Directory.GetFiles(bundledLibDir, "libavformat.so*").Length > 0)
        {
            ffmpeg.RootPath = bundledLibDir;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            ffmpeg.RootPath = FindFFmpegLibPath();
        }

        // Set up DllImport resolver for SDL2 and SDL2_ttf to load from bundled lib dir
        SetupNativeResolvers(bundledLibDir, libPath);

        try
        {
            using (var splash = new SplashScreen())
            {
                splash.Show();
            }

            PluginLoader.LoadPlugins();

            var playlist = new Playlist();
            double restorePositionSec = 0;
            SessionData? restoredSession = null;

            bool hasCLIMedia = mediaFiles.Count > 0 || playlistPath != null || urlListPath != null;

            if (!hasCLIMedia)
            {
                // No CLI input — try to restore previous session
                restoredSession = SessionManager.Load();
                if (restoredSession != null)
                {
                    var session = restoredSession;
                    playlist.AddRange(session.Entries.Select(e => new PlaylistEntry(e.Url, e.DisplayName, e.SourceUrl)));
                    playlist.MoveTo(session.CurrentIndex);
                    restorePositionSec = session.PositionSec;
                    if (Enum.TryParse<RepeatMode>(session.RepeatMode, out var rm))
                        playlist.RepeatMode = rm;
                    Console.Error.WriteLine($"[Session] Restored {playlist.Count} files from previous session");
                }
                else
                {
                    // No session — open file dialog
                    var picked = OpenFileDialogMultiple();
                    if (picked != null && picked.Count > 0)
                        playlist.AddRange(picked);

                    if (playlist.Count == 0)
                    {
                        Console.Error.WriteLine("No media files selected.");
                        return 1;
                    }
                    playlist.Reset();
                }
            }
            else
            {
                // Load from --playlist file (.m3u or .txt)
                if (playlistPath != null && File.Exists(playlistPath))
                {
                    playlist.LoadFromM3U(playlistPath);
                    Console.Error.WriteLine($"[Playlist] Loaded {playlist.Count} items from {playlistPath}");
                }

                // Load from --url-list file (text file with URLs, one per line)
                if (urlListPath != null && File.Exists(urlListPath))
                {
                    var urls = File.ReadAllLines(urlListPath)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
                        .ToList();

                    Console.Error.WriteLine($"[Playlist] Wczytano {urls.Count} URL-i z {urlListPath}");

                    foreach (var url in urls)
                    {
                        var plugin = PluginLoader.FindPluginForUrl(url);
                        if (plugin != null)
                        {
                            Console.Error.WriteLine($"[{plugin.Name}] Ekstrakcja linków z: {url}");
                            var entries = plugin.Resolve(url);
                            if (entries.Count > 0)
                            {
                                foreach (var e in entries)
                                    playlist.Add(e);
                                Console.Error.WriteLine($"[{plugin.Name}] Dodano {entries.Count} link(ów) HLS do playlisty");
                            }
                            else
                            {
                                Console.Error.WriteLine($"[{plugin.Name}] Nie znaleziono linków HLS dla: {url}");
                            }
                        }
                        else
                        {
                            playlist.Add(url);
                        }
                    }
                }

                // Add individual media files from CLI
                foreach (var f in mediaFiles)
                {
                    if (f == playlistPath) continue;

                    var plugin = PluginLoader.FindPluginForUrl(f);
                    if (plugin != null)
                    {
                        Console.Error.WriteLine($"[{plugin.Name}] Ekstrakcja linków z: {f}");
                        var entries = plugin.Resolve(f);
                        if (entries.Count > 0)
                        {
                            foreach (var e in entries)
                                playlist.Add(e);
                            Console.Error.WriteLine($"[{plugin.Name}] Dodano {entries.Count} link(ów) HLS do playlisty");
                        }
                        else
                        {
                            Console.Error.WriteLine($"[{plugin.Name}] Nie znaleziono linków HLS dla: {f}");
                        }
                    }
                    else
                    {
                        playlist.Add(f);
                    }
                }

                if (playlist.Count == 0)
                {
                    Console.Error.WriteLine("No media files found.");
                    PrintHelp();
                    return 1;
                }

                playlist.Reset();
            }

            using var player = new Player();
            player.UseHwAccel = useGpu;
            if (targetFps > 0) player.TargetFps = targetFps;
            if (subtitlePath != null) player.SubtitlePath = subtitlePath;
            player.Playlist = playlist;
            player.StartPositionSec = restorePositionSec;
            if (restoredSession != null)
            {
                player.RestoreVolume         = restoredSession.Volume;
                player.RestorePlaylistVisible = restoredSession.PlaylistVisible;
                player.RestoreWinX = restoredSession.WindowX;
                player.RestoreWinY = restoredSession.WindowY;
                player.RestoreWinW = restoredSession.WindowW;
                player.RestoreWinH = restoredSession.WindowH;
            }
            player.Play(playlist.Current, playlist.CurrentEntry);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex}");
            return 1;
        }
    }

    static List<string>? OpenFileDialogMultiple()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "zenity",
                Arguments = "--file-selection --multiple --separator='|' --title='Select Media Files' " +
                    "--file-filter='All files|*' " +
                    "--file-filter='Video|*.mp4 *.mkv *.avi *.mov *.webm *.flv *.wmv *.mpg *.mpeg *.m4v *.ts *.m2ts *.vob *.ogv *.3gp *.rm *.rmvb *.asf *.f4v *.dv' " +
                    "--file-filter='Audio|*.mp3 *.aac *.flac *.wav *.ogg *.opus *.m4a *.wma *.ac3 *.dts *.amr *.aiff *.alac'",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(15000);
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                return output.Split('|').Where(f => !string.IsNullOrEmpty(f) && File.Exists(f)).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Dialog] {ex.Message}");
        }
        return null;
    }

    static void SetupNativeResolvers(string bundledLibDir, string? userLibPath)
    {
        string searchDir = Directory.Exists(bundledLibDir) && File.Exists(Path.Combine(bundledLibDir, "libSDL2-2.0.so.0"))
            ? bundledLibDir
            : userLibPath ?? "";

        if (string.IsNullOrEmpty(searchDir))
            return;

        NativeLibrary.SetDllImportResolver(typeof(SDL).Assembly, (name, assembly, path) =>
        {
            return ResolveNativeLib(name, searchDir);
        });

        NativeLibrary.SetDllImportResolver(typeof(SDLTtf).Assembly, (name, assembly, path) =>
        {
            return ResolveNativeLib(name, searchDir);
        });
    }

    static IntPtr ResolveNativeLib(string name, string searchDir)
    {
        // Try exact name first
        string exact = Path.Combine(searchDir, name);
        if (NativeLibrary.TryLoad(exact, out IntPtr handle))
            return handle;

        // Try lib{name}.so
        string libSo = Path.Combine(searchDir, $"lib{name}.so");
        if (NativeLibrary.TryLoad(libSo, out handle))
            return handle;

        // Try lib{name}.so.0
        string libSo0 = Path.Combine(searchDir, $"lib{name}.so.0");
        if (NativeLibrary.TryLoad(libSo0, out handle))
            return handle;

        // Try lib{name}-2.0.so.0 (for SDL2)
        string lib200 = Path.Combine(searchDir, $"lib{name}-2.0.so.0");
        if (NativeLibrary.TryLoad(lib200, out handle))
            return handle;

        // Fallback to default search
        NativeLibrary.TryLoad(name, out handle);
        return handle;
    }

    static string FindFFmpegLibPath()
    {
        string[] candidates =
        [
            "/usr/lib/x86_64-linux-gnu",
            "/usr/lib",
            "/usr/local/lib",
            "/lib/x86_64-linux-gnu",
            "/lib",
        ];
        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir) && Directory.GetFiles(dir, "libavformat.so*").Length > 0)
                return dir;
        }
        return "";
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
        CSharp FFmpeg Player — video & audio player with GPU acceleration

        USAGE:
            csharp-ffmpeg-player <file...> [OPTIONS]
            csharp-ffmpeg-player --playlist <list.m3u> [OPTIONS]

        OPTIONS:
            -gpu, --gpu          Enable GPU hardware acceleration (VAAPI/CUDA/VDPAU)
            --fps N, -fps N      Target render frame rate (e.g. 30, 60, 120, 144)
            --subtitle PATH      Load subtitle file (.srt, .sub, .txt)
            -sub PATH            Alias for --subtitle
            --playlist PATH      Load playlist file (.m3u, .txt — one file per line)
            -pl PATH             Alias for --playlist
            --url-list PATH     Load URLs from text file (one URL per line, plugin-resolved)
            -ul PATH            Alias for --url-list
            --help, -h           Show this help message

            <file...>            One or more media files to play sequentially
            <lib-path>           Path to FFmpeg/SDL2 libraries (auto-detected if omitted)

        EXAMPLES:
            csharp-ffmpeg-player video.mp4
            csharp-ffmpeg-player video.mp4 -gpu
            csharp-ffmpeg-player video.mp4 -gpu --fps 60
            csharp-ffmpeg-player video.mp4 --subtitle subs.srt
            csharp-ffmpeg-player video1.mp4 video2.mp4 video3.mp4 -gpu
            csharp-ffmpeg-player --playlist mylist.m3u -gpu
            csharp-ffmpeg-player audio.mp3
            csharp-ffmpeg-player video.mp4 /usr/lib/x86_64-linux-gnu -gpu
            csharp-ffmpeg-player https://example.com/stream.m3u8
            csharp-ffmpeg-player --url-list urls.txt -gpu

        KEYBOARD CONTROLS:
            Space                Play / Pause
            Left / Right         Seek -10s / +10s
            N                    Next track in playlist
            P                    Previous track in playlist
            ESC                  Exit fullscreen (or quit if not fullscreen)
            Q                    Quit

        MOUSE CONTROLS:
            Click progress bar   Seek to position
            Double-click video   Toggle fullscreen
            Hover buttons        Play/Pause, Stop, Open File

        SUPPORTED FORMATS:
            Video: mp4 mkv avi mov webm flv wmv mpg mpeg m4v ts m2ts vob ogv 3gp rm asf ...
            Audio: mp3 aac flac wav ogg opus m4a wma ac3 aiff ...
        """);
    }
}
