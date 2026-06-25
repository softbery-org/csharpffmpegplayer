using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpFFmpeg;

public sealed class SessionData
{
    [JsonPropertyName("files")]
    public List<string> Files { get; set; } = new();

    [JsonPropertyName("currentIndex")]
    public int CurrentIndex { get; set; } = 0;

    [JsonPropertyName("positionSec")]
    public double PositionSec { get; set; } = 0;

    [JsonPropertyName("repeatMode")]
    public string RepeatMode { get; set; } = "Once";

    [JsonPropertyName("volume")]
    public float Volume { get; set; } = 1.0f;

    [JsonPropertyName("playlistVisible")]
    public bool PlaylistVisible { get; set; } = false;

    [JsonPropertyName("windowX")]
    public int WindowX { get; set; } = -1;

    [JsonPropertyName("windowY")]
    public int WindowY { get; set; } = -1;

    [JsonPropertyName("windowW")]
    public int WindowW { get; set; } = 0;

    [JsonPropertyName("windowH")]
    public int WindowH { get; set; } = 0;
}

public static class SessionManager
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "csharp-ffmpeg-player");

    private static readonly string SessionFile =
        Path.Combine(ConfigDir, "session.json");

    public static void Save(Playlist playlist, double positionSec, float volume,
        bool playlistVisible, int winX, int winY, int winW, int winH)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var data = new SessionData
            {
                Files          = playlist.Files.ToList(),
                CurrentIndex   = playlist.CurrentIndex,
                PositionSec    = positionSec,
                RepeatMode     = playlist.RepeatMode.ToString(),
                Volume         = volume,
                PlaylistVisible = playlistVisible,
                WindowX        = winX,
                WindowY        = winY,
                WindowW        = winW,
                WindowH        = winH,
            };
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SessionFile, json);
            Console.Error.WriteLine($"[Session] Saved {data.Files.Count} files, idx={data.CurrentIndex}, pos={positionSec:F1}s, vol={volume:F2}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Session] Save failed: {ex.Message}");
        }
    }

    public static SessionData? Load()
    {
        try
        {
            if (!File.Exists(SessionFile)) return null;
            string json = File.ReadAllText(SessionFile);
            var data = JsonSerializer.Deserialize<SessionData>(json);
            if (data == null) return null;
            // Warn about missing files but keep them in list (removable media may reconnect)
            int missing = data.Files.Count(f => !File.Exists(f));
            if (missing > 0)
                Console.Error.WriteLine($"[Session] Warning: {missing} file(s) not found on disk");
            // Only drop if ALL files are missing
            var existing = data.Files.Where(File.Exists).ToList();
            if (existing.Count == 0) return null;
            data.Files = existing;
            data.CurrentIndex = Math.Clamp(data.CurrentIndex, 0, data.Files.Count - 1);
            Console.Error.WriteLine($"[Session] Loaded {data.Files.Count} files, idx={data.CurrentIndex}, pos={data.PositionSec:F1}s, vol={data.Volume:F2}");
            return data;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Session] Load failed: {ex.Message}");
            return null;
        }
    }

    public static bool HasSession() => File.Exists(SessionFile);
}
