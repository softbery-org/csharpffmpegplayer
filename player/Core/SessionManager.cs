using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpFFmpeg;

public sealed class SessionEntry
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; set; }
}

public sealed class SessionData
{
    [JsonPropertyName("entries")]
    public List<SessionEntry> Entries { get; set; } = new();

    [JsonPropertyName("files")]
    public List<string>? LegacyFiles { get; set; }

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
                Entries         = playlist.Entries.Select(e => new SessionEntry
                {
                    Url = e.Url,
                    DisplayName = e.DisplayName,
                    SourceUrl = e.SourceUrl,
                }).ToList(),
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
            Console.Error.WriteLine($"[Session] Saved {data.Entries.Count} files, idx={data.CurrentIndex}, pos={positionSec:F1}s, vol={volume:F2}");
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

            // Migrate legacy flat file list
            if (data.Entries.Count == 0 && data.LegacyFiles != null && data.LegacyFiles.Count > 0)
            {
                data.Entries = data.LegacyFiles
                    .Where(f => string.IsNullOrEmpty(f) || File.Exists(f) || f.StartsWith("http"))
                    .Select(f => new SessionEntry { Url = f, DisplayName = PlaylistEntry.ExtractDisplayName(f) })
                    .ToList();
                data.LegacyFiles = null;
            }

            // Warn about missing local files but keep URLs
            int missing = data.Entries.Count(e =>
                !string.IsNullOrEmpty(e.Url) &&
                !e.Url.StartsWith("http") &&
                !File.Exists(e.Url));
            if (missing > 0)
                Console.Error.WriteLine($"[Session] Warning: {missing} file(s) not found on disk");

            var valid = data.Entries.Where(e =>
                string.IsNullOrEmpty(e.Url) ||
                e.Url.StartsWith("http") ||
                File.Exists(e.Url)).ToList();
            if (valid.Count == 0) return null;
            data.Entries = valid;
            data.CurrentIndex = Math.Clamp(data.CurrentIndex, 0, data.Entries.Count - 1);
            Console.Error.WriteLine($"[Session] Loaded {data.Entries.Count} files, idx={data.CurrentIndex}, pos={data.PositionSec:F1}s, vol={data.Volume:F2}");
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
