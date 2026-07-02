namespace CSharpFFmpeg;

public sealed class PlaylistEntry
{
    public string Url { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? SourceUrl { get; set; }

    public PlaylistEntry() { }

    public PlaylistEntry(string url, string? displayName = null, string? sourceUrl = null)
    {
        Url = url;
        DisplayName = displayName ?? ExtractDisplayName(url);
        SourceUrl = sourceUrl;
    }

    public static string ExtractDisplayName(string url)
    {
        // Never expose server stream URLs
        if (url.Contains("/api/media/") && url.Contains("/stream"))
            return "Media";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Try to extract a readable name from the URL path
            var uri = new Uri(url);
            string lastSegment = System.IO.Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            if (!string.IsNullOrEmpty(lastSegment))
                return lastSegment;
            return uri.Host;
        }
        return System.IO.Path.GetFileNameWithoutExtension(url);
    }

    public override string ToString() => DisplayName;
}
