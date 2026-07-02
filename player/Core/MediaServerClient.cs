using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CSharpFFmpeg;

public class MediaServerClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _apiKey;

    public string? ServerUrl { get; private set; }
    public string? Username { get; private set; }
    public bool IsConnected => _apiKey != null && ServerUrl != null;

    public string? GetApiKey() => _apiKey;

    public MediaServerClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string serverUrl)
    {
        ServerUrl = serverUrl.TrimEnd('/');
        _http.BaseAddress = new Uri(ServerUrl);
    }

    public async Task<bool> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        if (ServerUrl == null) throw new InvalidOperationException("Server not configured. Call Configure() first.");
        var resp = await _http.PostAsync(
            $"/api/auth/login?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}", null, ct);
        if (!resp.IsSuccessStatusCode) return false;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        _apiKey = doc.RootElement.GetProperty("api_key").GetString();
        Username = doc.RootElement.GetProperty("username").GetString();
        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
        return true;
    }

    public void SetApiKey(string apiKey)
    {
        _apiKey = apiKey;
        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
    }

    public async Task<List<RemoteMedia>> ListMediaAsync(int limit = 100, int offset = 0, string? mediaType = null, CancellationToken ct = default)
    {
        var url = $"/api/media?limit={limit}&offset={offset}";
        if (mediaType != null) url += $"&media_type={Uri.EscapeDataString(mediaType)}";
        return await FetchMediaList(url, ct);
    }

    public async Task<List<RemoteMedia>> ListMediaFilteredAsync(int limit = 100, int offset = 0, string? year = null, string? country = null, string? genre = null, CancellationToken ct = default)
    {
        var url = $"/api/media?limit={limit}&offset={offset}";
        if (year != null) url += $"&year={Uri.EscapeDataString(year)}";
        if (country != null) url += $"&country={Uri.EscapeDataString(country)}";
        if (genre != null) url += $"&genre={Uri.EscapeDataString(genre)}";
        return await FetchMediaList(url, ct);
    }

    private async Task<List<RemoteMedia>> FetchMediaList(string url, CancellationToken ct)
    {
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"[Server] ListMedia failed: {resp.StatusCode} {resp.ReasonPhrase}");
            return new List<RemoteMedia>();
        }
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<RemoteMedia>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            list.Add(RemoteMedia.FromJson(item));
        }
        return list;
    }

    public async Task<MediaFilters?> GetFiltersAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("/api/media/filters", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var filters = new MediaFilters
        {
            Years = new List<string>(),
            Countries = new List<string>(),
            Genres = new List<string>(),
            MediaTypes = new List<string>(),
        };
        if (root.TryGetProperty("years", out var years))
            foreach (var y in years.EnumerateArray())
                filters.Years.Add(y.GetString() ?? "");
        if (root.TryGetProperty("countries", out var countries))
            foreach (var c in countries.EnumerateArray())
                filters.Countries.Add(c.GetString() ?? "");
        if (root.TryGetProperty("genres", out var genres))
            foreach (var g in genres.EnumerateArray())
                filters.Genres.Add(g.GetString() ?? "");
        if (root.TryGetProperty("media_types", out var mediaTypes))
            foreach (var mt in mediaTypes.EnumerateArray())
                filters.MediaTypes.Add(mt.GetString() ?? "");
        return filters;
    }

    public async Task<RemoteMedia?> GetMediaAsync(int mediaId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/media/{mediaId}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return RemoteMedia.FromJson(doc.RootElement);
    }

    public string GetStreamUrl(int mediaId)
    {
        if (ServerUrl == null || _apiKey == null)
            throw new InvalidOperationException("Not connected to server.");
        return $"{ServerUrl}/api/media/{mediaId}/stream?api_key={Uri.EscapeDataString(_apiKey)}";
    }

    public async Task<ImportResult> ImportSeriesAsync(string seriesUrl, string? versionFilter = null, CancellationToken ct = default)
    {
        var url = $"/api/media/import-series?url={Uri.EscapeDataString(seriesUrl)}";
        if (versionFilter != null)
            url += $"&version_filter={Uri.EscapeDataString(versionFilter)}";
        var resp = await _http.PostAsync(url, null, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return new ImportResult
        {
            Status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
            Series = doc.RootElement.TryGetProperty("series", out var sr) ? sr.GetString() ?? "" : "",
            EpisodesFound = doc.RootElement.TryGetProperty("episodes_found", out var ef) ? ef.GetInt32() : 0,
            LinksAdded = doc.RootElement.TryGetProperty("links_added", out var la) ? la.GetInt32() : 0,
            SkippedDuplicates = doc.RootElement.TryGetProperty("skipped_duplicates", out var sd) ? sd.GetInt32() : 0,
        };
    }

    public async Task<List<RemotePlaylist>> ListPlaylistsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("/api/playlists", ct);
        if (!resp.IsSuccessStatusCode) return new List<RemotePlaylist>();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<RemotePlaylist>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            list.Add(new RemotePlaylist
            {
                Id = item.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                Description = item.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                CreatedAt = item.TryGetProperty("created_at", out var ca) ? ca.GetString() ?? "" : "",
            });
        }
        return list;
    }

    public async Task<RemotePlaylistDetail?> GetPlaylistAsync(int playlistId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/playlists/{playlistId}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var pl = doc.RootElement.GetProperty("playlist");
        var items = doc.RootElement.GetProperty("items");
        var detail = new RemotePlaylistDetail
        {
            Id = pl.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
            Name = pl.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            Description = pl.TryGetProperty("description", out var desc) ? desc.GetString() : null,
            Items = new List<RemoteMedia>(),
        };
        foreach (var item in items.EnumerateArray())
        {
            detail.Items.Add(RemoteMedia.FromJson(item));
        }
        return detail;
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    public async Task<SeriesDetail?> GetSeriesDetailAsync(string seriesName, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/series/{Uri.EscapeDataString(seriesName)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var detail = new SeriesDetail
        {
            SeriesName = root.TryGetProperty("series_name", out var sn) ? sn.GetString() ?? "" : "",
            TotalLinks = root.TryGetProperty("total_links", out var tl) ? tl.GetInt32() : 0,
            Versions = new List<string>(),
            Seasons = new List<SeriesSeason>(),
        };
        if (root.TryGetProperty("versions", out var vers))
            foreach (var v in vers.EnumerateArray())
                detail.Versions.Add(v.GetString() ?? "");
        if (root.TryGetProperty("seasons", out var seasons))
            foreach (var s in seasons.EnumerateArray())
                detail.Seasons.Add(new SeriesSeason
                {
                    Season = s.TryGetProperty("season", out var sv) ? sv.GetInt32() : 0,
                    EpisodeCount = s.TryGetProperty("episode_count", out var ec) ? ec.GetInt32() : 0,
                    LinkCount = s.TryGetProperty("link_count", out var lc) ? lc.GetInt32() : 0,
                });
        return detail;
    }

    public async Task<SeriesInfo?> GetSeriesInfoAsync(string filmanUrl, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/series/info?url={Uri.EscapeDataString(filmanUrl)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = new SeriesInfo
        {
            Url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
            Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            Description = root.TryGetProperty("description", out var d) ? d.GetString() : null,
            Poster = root.TryGetProperty("poster", out var p) ? p.GetString() : null,
            Year = root.TryGetProperty("year", out var y) ? y.GetString() : null,
            Rating = root.TryGetProperty("rating", out var r) ? r.GetString() : null,
            Votes = root.TryGetProperty("votes", out var v) ? v.GetString() : null,
            ImdbRating = root.TryGetProperty("imdb_rating", out var ir) ? ir.GetString() : null,
            ImdbVotes = root.TryGetProperty("imdb_votes", out var iv) ? iv.GetString() : null,
            Views = root.TryGetProperty("views", out var vw) ? vw.GetString() : null,
            DurationText = root.TryGetProperty("duration_text", out var dur) ? dur.GetString() : null,
            Genres = new List<string>(),
            Countries = new List<string>(),
        };
        if (root.TryGetProperty("genres", out var genres))
            foreach (var g in genres.EnumerateArray())
                info.Genres.Add(g.GetString() ?? "");
        if (root.TryGetProperty("countries", out var countries))
            foreach (var c in countries.EnumerateArray())
                info.Countries.Add(c.GetString() ?? "");
        return info;
    }

    public async Task<EpisodeDetail?> GetEpisodeInfoAsync(string filmanUrl, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/episode/info?url={Uri.EscapeDataString(filmanUrl)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = new EpisodeDetail
        {
            Url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
            Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            Description = root.TryGetProperty("description", out var d) ? d.GetString() : null,
            Screenshot = root.TryGetProperty("screenshot", out var s) ? s.GetString() : null,
            Season = root.TryGetProperty("season", out var se) && se.ValueKind == JsonValueKind.Number ? se.GetInt32() : null,
            Episode = root.TryGetProperty("episode", out var ep) && ep.ValueKind == JsonValueKind.Number ? ep.GetInt32() : null,
            AvailableSources = new List<EpisodeSourceInfo>(),
        };
        if (root.TryGetProperty("available_sources", out var sources))
            foreach (var src in sources.EnumerateArray())
                info.AvailableSources.Add(new EpisodeSourceInfo
                {
                    Source = src.TryGetProperty("source", out var sname) ? sname.GetString() ?? "" : "",
                    Version = src.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "",
                });
        return info;
    }
}

public class RemoteMedia
{
    public int Id { get; set; }
    public string Filename { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string MediaType { get; set; } = "video";
    public long FileSize { get; set; }
    public string MimeType { get; set; } = "video/mp4";
    public double? Duration { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string UploadedAt { get; set; } = "";
    public string? LinkUrl { get; set; }
    public string? Version { get; set; }
    public string? SourceName { get; set; }
    public string? SeriesName { get; set; }
    public string? Year { get; set; }
    public string? Country { get; set; }
    public string? Genres { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? EpisodeTitle { get; set; }

    public string DisplayName => !string.IsNullOrEmpty(Title) ? Title : Filename;

    public static RemoteMedia FromJson(JsonElement el)
    {
        return new RemoteMedia
        {
            Id = el.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
            Filename = el.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "" : "",
            Title = el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            Description = el.TryGetProperty("description", out var d) ? d.GetString() : null,
            MediaType = el.TryGetProperty("media_type", out var mt) ? mt.GetString() ?? "video" : "video",
            FileSize = el.TryGetProperty("file_size", out var fs) ? fs.GetInt64() : 0,
            MimeType = el.TryGetProperty("mime_type", out var mime) ? mime.GetString() ?? "video/mp4" : "video/mp4",
            Duration = el.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetDouble() : null,
            Width = el.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : null,
            Height = el.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : null,
            UploadedAt = el.TryGetProperty("uploaded_at", out var ua) ? ua.GetString() ?? "" : "",
            Version = el.TryGetProperty("version", out var v) ? v.GetString() : null,
            SourceName = el.TryGetProperty("source_name", out var sn) ? sn.GetString() : null,
            SeriesName = el.TryGetProperty("series_name", out var sname) ? sname.GetString() : null,
            LinkUrl = el.TryGetProperty("link_url", out var lu) ? lu.GetString() : null,
            Year = el.TryGetProperty("year", out var yr) ? yr.GetString() : null,
            Country = el.TryGetProperty("country", out var ctr) ? ctr.GetString() : null,
            Genres = el.TryGetProperty("genres", out var gn) ? gn.GetString() : null,
            SeasonNumber = el.TryGetProperty("season_number", out var sn2) && sn2.ValueKind == JsonValueKind.Number ? sn2.GetInt32() : null,
            EpisodeNumber = el.TryGetProperty("episode_number", out var en) && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : null,
            EpisodeTitle = el.TryGetProperty("episode_title", out var et) ? et.GetString() : null,
        };
    }
}

public class MediaFilters
{
    public List<string> Years { get; set; } = new();
    public List<string> Countries { get; set; } = new();
    public List<string> Genres { get; set; } = new();
    public List<string> MediaTypes { get; set; } = new();
}

public class ImportResult
{
    public string Status { get; set; } = "";
    public string Series { get; set; } = "";
    public int EpisodesFound { get; set; }
    public int LinksAdded { get; set; }
    public int SkippedDuplicates { get; set; }
}

public class RemotePlaylist
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string CreatedAt { get; set; } = "";
}

public class RemotePlaylistDetail : RemotePlaylist
{
    public List<RemoteMedia> Items { get; set; } = new();
}

public class SeriesDetail
{
    public string SeriesName { get; set; } = "";
    public int TotalLinks { get; set; }
    public List<string> Versions { get; set; } = new();
    public List<SeriesSeason> Seasons { get; set; } = new();
}

public class SeriesSeason
{
    public int Season { get; set; }
    public int EpisodeCount { get; set; }
    public int LinkCount { get; set; }
}

public class SeriesInfo
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Poster { get; set; }
    public string? Year { get; set; }
    public string? Rating { get; set; }
    public string? Votes { get; set; }
    public string? ImdbRating { get; set; }
    public string? ImdbVotes { get; set; }
    public string? Views { get; set; }
    public string? DurationText { get; set; }
    public List<string> Countries { get; set; } = new();
    public List<string> Genres { get; set; } = new();
}

public class EpisodeDetail
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Screenshot { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public List<EpisodeSourceInfo> AvailableSources { get; set; } = new();
}

public class EpisodeSourceInfo
{
    public string Source { get; set; } = "";
    public string Version { get; set; } = "";
}
