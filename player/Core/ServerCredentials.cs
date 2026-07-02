using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpFFmpeg;

public sealed class ServerCredentials
{
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}

public static class ServerCredentialsManager
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "csharp-ffmpeg-player");

    private static readonly string CredsFile =
        Path.Combine(ConfigDir, "server-creds.json");

    public static void Save(ServerCredentials creds)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            string json = JsonSerializer.Serialize(creds, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CredsFile, json);
            Console.Error.WriteLine($"[Server] Saved credentials for {creds.Username} @ {creds.ServerUrl}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Server] Save credentials failed: {ex.Message}");
        }
    }

    public static ServerCredentials? Load()
    {
        try
        {
            if (!File.Exists(CredsFile)) return null;
            string json = File.ReadAllText(CredsFile);
            var creds = JsonSerializer.Deserialize<ServerCredentials>(json);
            if (creds == null || string.IsNullOrEmpty(creds.ServerUrl)) return null;
            return creds;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Server] Load credentials failed: {ex.Message}");
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(CredsFile))
                File.Delete(CredsFile);
        }
        catch { }
    }
}
