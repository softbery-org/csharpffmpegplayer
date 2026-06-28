using Subtitles;

namespace CSharpFFmpeg;

public sealed partial class Player
{
    private void OpenNewFile(string filePath, PlaylistEntry? entry = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            Console.Error.WriteLine("[Player] OpenNewFile: empty filePath, skipping");
            return;
        }
        _lastReopenTicks = Environment.TickCount64;
        Console.Error.WriteLine($"[Player] OpenNewFile: {filePath}");

        _decodeRunning = false;
        _renderer.StopAudio();
        lock (_videoLock) { Monitor.PulseAll(_videoLock); }
        lock (_audioLock) { Monitor.PulseAll(_audioLock); }
        if (_decodeThread != null && _decodeThread.IsAlive)
        {
            bool joined = _decodeThread.Join(3000);
            Console.Error.WriteLine($"[Player] DecodeThread joined={joined}");
        }

        ClearQueues();
        _lastFrame?.Dispose();
        _lastFrame = null;

        _decoder.Dispose();
        Console.Error.WriteLine($"[Player] Decoder disposed, opening: {filePath}");

        _running = true;
        _decodeRunning = true;
        _trackEof = false;
        _paused = false;
        _clockStarted = false;
        _stopped = false;
        _trackEnded = false;
        _clock.Reset();

        try
        {
            _decoder.UseHwAccel = _useHwAccel;
            _decoder.Open(filePath);
            Console.Error.WriteLine($"[Player] Opened: video={_decoder.VideoStreamIndex} audio={_decoder.AudioStreamIndex} dur={_decoder.DurationSec:F1}s");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Player] Open failed: {ex.Message}");

            if (entry != null && !string.IsNullOrEmpty(entry.SourceUrl))
            {
                var plugin = PluginLoader.FindPluginForUrl(entry.SourceUrl);
                if (plugin != null)
                {
                    Console.Error.WriteLine($"[Player] Re-extracting from SourceUrl: {entry.SourceUrl}");
                    try
                    {
                        var newEntries = plugin.Resolve(entry.SourceUrl);
                        if (newEntries.Count > 0)
                        {
                            var fresh = newEntries[0];
                            Console.Error.WriteLine($"[Player] Got fresh URL: {fresh.Url}");
                            entry.Url = fresh.Url;
                            if (!string.IsNullOrEmpty(fresh.DisplayName))
                                entry.DisplayName = fresh.DisplayName;
                            _decoder.Open(fresh.Url);
                            Console.Error.WriteLine($"[Player] Re-opened: video={_decoder.VideoStreamIndex} audio={_decoder.AudioStreamIndex} dur={_decoder.DurationSec:F1}s");
                            goto opened;
                        }
                    }
                    catch (Exception ex2)
                    {
                        Console.Error.WriteLine($"[Player] Re-extraction failed: {ex2.Message}");
                    }
                }
            }

            _trackEnded = true;
            _decodeRunning = false;
            _renderer.ShowError($"Błąd odtwarzania:\n{Path.GetFileName(filePath)}\n{ex.Message}");
            return;
        }

        opened:

        _videoWidth  = _decoder.VideoWidth;
        _videoHeight = _decoder.VideoHeight;
        _duration    = _decoder.DurationSec;
        _videoPath   = filePath;

        bool videoOk = _decoder.VideoStreamIndex < 0 || (_videoWidth > 0 && _videoHeight > 0);
        bool audioOk = _decoder.AudioStreamIndex < 0 || _decoder.SampleRate > 0;
        if (!videoOk || !audioOk)
        {
            string reason = !videoOk ? $"nieprawidłowa rozdzielczość ({_videoWidth}x{_videoHeight})"
                                     : $"nieprawidłowy format audio (rate={_decoder.SampleRate})";
            Console.Error.WriteLine($"[Player] Skipping broken file: {reason} — {filePath}");
            _renderer.ShowError($"Błąd odtwarzania:\n{Path.GetFileName(filePath)}\n{reason}");
            _decodeRunning = false;
            _trackEnded = false;
            _trackEof   = true;
            _clockStarted = true;
            return;
        }

        string? title = entry?.DisplayName;
        if (string.IsNullOrEmpty(title))
            title = Path.GetFileNameWithoutExtension(filePath);
        if (_decoder.VideoStreamIndex >= 0)
            _renderer.ReinitForNewFile(_videoWidth, _videoHeight, _duration, title);
        else
            _renderer.ReinitForNewFile(0, 0, _duration, title);

        TryLoadSubtitles(filePath);

        if (_decoder.AudioStreamIndex >= 0)
        {
            Console.Error.WriteLine($"[Player] InitAudio rate={_decoder.SampleRate}");
            _renderer.InitAudio(_decoder.SampleRate, 2, OnAudioCallback);
        }

        _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "Decode" };
        _decodeThread.Start();
        _trackOpenTicks = Environment.TickCount64;
        Console.Error.WriteLine($"[Player] DecodeThread started");
    }

    private void TryLoadSubtitles(string videoPath)
    {
        _subtitles = null;
        _renderer.SubtitleText = null;

        if (!string.IsNullOrEmpty(SubtitlePath))
        {
            if (File.Exists(SubtitlePath))
            {
                try
                {
                    _subtitles = new SubtitleManager(SubtitlePath);
                    Console.Error.WriteLine($"[Subtitles] Loaded: {SubtitlePath} ({_subtitles.Count} entries)");
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Subtitles] Failed to load {SubtitlePath}: {ex.Message}");
                }
            }
            else
            {
                Console.Error.WriteLine($"[Subtitles] File not found: {SubtitlePath}");
            }
        }

        string baseName = System.IO.Path.GetFileNameWithoutExtension(videoPath);
        string dir = System.IO.Path.GetDirectoryName(videoPath) ?? "";
        string[] subExts = { ".srt", ".sub", ".txt" };

        foreach (var ext in subExts)
        {
            string subPath = System.IO.Path.Combine(dir, baseName + ext);
            if (File.Exists(subPath))
            {
                try
                {
                    _subtitles = new SubtitleManager(subPath);
                    Console.Error.WriteLine($"[Subtitles] Loaded: {subPath} ({_subtitles.Count} entries)");
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Subtitles] Failed to load {subPath}: {ex.Message}");
                }
            }
        }
    }

    private void UpdateSubtitle()
    {
        if (_subtitles == null || _subtitles.Count == 0)
        {
            _renderer.SubtitleText = null;
            return;
        }

        double clock = GetMasterClock();
        var sub = _subtitles.GetSubtitleAtTime(TimeSpan.FromSeconds(clock));
        _renderer.SubtitleText = sub != null ? string.Join("\n", sub.TextLines) : null;
    }

    private void ScrollPlaylistToSelected()
    {
        _renderer.ScrollPlaylistToIndex(_renderer.PlaylistSelectedIndex);
    }

    private void HandleContextMenuAction(SDLRenderer.ContextMenuAction action, int itemIndex)
    {
        if (Playlist == null || itemIndex < 0 || itemIndex >= Playlist.Count) return;
        switch (action)
        {
            case SDLRenderer.ContextMenuAction.Play:
                Playlist.MoveTo(itemIndex);
                OpenNewFile(Playlist.Current, Playlist.CurrentEntry);
                break;
            case SDLRenderer.ContextMenuAction.PlayNext:
                if (itemIndex != Playlist.CurrentIndex)
                {
                    int insertAt = Playlist.CurrentIndex + 1;
                    if (insertAt > itemIndex) insertAt--;
                    Playlist.Move(itemIndex, Math.Clamp(insertAt, 0, Playlist.Count - 1));
                }
                break;
            case SDLRenderer.ContextMenuAction.RemoveFromPlaylist:
                Playlist.RemoveAt(itemIndex);
                break;
        }
    }

    private void CycleRepeatMode()
    {
        if (Playlist == null) return;
        Playlist.RepeatMode = Playlist.RepeatMode switch
        {
            RepeatMode.Once    => RepeatMode.All,
            RepeatMode.All     => RepeatMode.One,
            RepeatMode.One     => RepeatMode.Shuffle,
            RepeatMode.Shuffle => RepeatMode.Once,
            _                  => RepeatMode.Once,
        };
    }

    private void PlayNext()
    {
        if (Playlist == null || !Playlist.HasNext) return;
        Playlist.MoveNext();
        OpenNewFile(Playlist.Current, Playlist.CurrentEntry);
    }

    private void PlayPrev()
    {
        if (Playlist == null || !Playlist.HasPrev) return;
        Playlist.MovePrev();
        OpenNewFile(Playlist.Current, Playlist.CurrentEntry);
    }

    private void UpdatePlaylistPanel()
    {
        if (Playlist == null) return;
        if (Playlist.Count == 0)
        {
            _renderer.SetPlaylistData(Array.Empty<string>(), Array.Empty<double>(), -1, 0);
            return;
        }
        var entries = Playlist.Entries;
        var names = new string[entries.Count];
        var durations = new double[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            names[i] = entries[i].DisplayName;
            durations[i] = i == Playlist.CurrentIndex ? _duration : 0;
        }
        _renderer.SetPlaylistData(names, durations, Playlist.CurrentIndex, GetMasterClock());
    }
}
