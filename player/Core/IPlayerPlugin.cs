namespace CSharpFFmpeg;

public interface IPlayerPlugin
{
    string Name { get; }
    string Description { get; }

    bool CanHandle(string url);
    List<PlaylistEntry> Resolve(string url);
}

/// <summary>
/// Optional interface for plugins that support series/episode extraction.
/// </summary>
public interface ISeriesPlugin
{
    /// <summary>
    /// Gets list of episodes from a series page URL.
    /// </summary>
    List<EpisodeInfo> GetEpisodes(string url);

    /// <summary>
    /// Resolves a single episode URL to playable stream entries.
    /// </summary>
    List<PlaylistEntry> ResolveEpisode(string episodeUrl, string? episodeTitle = null);
}

public class EpisodeInfo
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int Number { get; set; }
}
