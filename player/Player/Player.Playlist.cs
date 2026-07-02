using Subtitles;

namespace CSharpFFmpeg;

public sealed partial class Player
{
    private bool OpenNewFile(string filePath, PlaylistEntry? entry = null, int attempt = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath == "separator://")
        {
            Console.Error.WriteLine("[Player] OpenNewFile: skipping separator or empty filePath");
            return false;
        }
        if (filePath.StartsWith("serveryear://"))
        {
            var year = filePath.Substring("serveryear://".Length);
            LoadServerYear(year);
            return false;
        }
        if (filePath.StartsWith("servermediatype://"))
        {
            var mediaType = Uri.UnescapeDataString(filePath.Substring("servermediatype://".Length));
            LoadServerMediaType(mediaType);
            return false;
        }
        if (filePath.StartsWith("serverplaylist://"))
        {
            var playlistIdStr = filePath.Substring("serverplaylist://".Length);
            if (int.TryParse(playlistIdStr, out int playlistId))
            {
                LoadServerPlaylist(playlistId);
            }
            return false;
        }
        if (filePath == "serverseries://")
        {
            // Show series playlists (expand the Seriale category)
            LoadServerSeriesList();
            return false;
        }
        if (filePath.StartsWith("serverseason://"))
        {
            var parts = filePath.Substring("serverseason://".Length).Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int pid) && int.TryParse(parts[1], out int season))
            {
                LoadServerSeason(pid, season);
            }
            return false;
        }
        if (filePath.StartsWith("serversource://"))
        {
            var rest = filePath.Substring("serversource://".Length);
            var firstColon = rest.IndexOf(':');
            var secondColon = rest.IndexOf(':', firstColon + 1);
            if (firstColon > 0 && secondColon > 0)
            {
                if (int.TryParse(rest.Substring(0, firstColon), out int pid) &&
                    int.TryParse(rest.Substring(firstColon + 1, secondColon - firstColon - 1), out int season))
                {
                    string srcName = rest.Substring(secondColon + 1);
                    LoadServerSource(pid, season, srcName);
                }
            }
            return false;
        }
        if (filePath.StartsWith("servermoviesource://"))
        {
            var rest = filePath.Substring("servermoviesource://".Length);
            var firstColon = rest.IndexOf(':');
            if (firstColon > 0 && int.TryParse(rest.Substring(0, firstColon), out int pid))
            {
                string srcName = Uri.UnescapeDataString(rest.Substring(firstColon + 1));
                LoadServerMovieSource(pid, srcName);
            }
            return false;
        }
        if (filePath == "serverback://")
        {
            // Go back to playlist list — re-trigger SRV browse
            _renderer.SeriesInfoVisible = false;
            _renderer.EpisodeInfoVisible = false;
            _renderer.ClearSeriesPoster();
            _forceRedraw = true;
            // Re-fetch playlist list from server
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var playlists = ServerClient.ListPlaylistsAsync().Result;
                    BuildServerPlaylistList(playlists);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Server] Back error: {ex.Message}");
                }
            });
            return false;
        }
        if (_asyncOpenPending)
        {
            if (filePath == _asyncOpenFilePath)
                return false; // same file already opening, ignore silently
            // Previous open is pending but different — cancel it and proceed
            Console.Error.WriteLine("[Player] OpenNewFile: cancelling previous pending open");
            _asyncOpenPending = false;
        }

        // Cooldown: if previous open was replaced (still running), wait before starting new one
        if (_asyncOpenRunning && Environment.TickCount64 - _lastReopenTicks < 500)
        {
            Console.Error.WriteLine("[Player] OpenNewFile: cooldown (previous open still running), ignoring");
            return false;
        }

        _lastReopenTicks = Environment.TickCount64;
        _asyncOpenFilePath = filePath;
        _asyncOpenEntry = entry;
        _asyncOpenAttempt = attempt;
        _asyncOpenPending = true;
        _asyncOpenComplete = false;
        _asyncOpenSuccess = false;
        _asyncOpenError = null;

        CleanupBeforeOpen();
        _cancelOpen = false;  // Reset cancel AFTER cleanup so the new open can proceed
        Console.Error.WriteLine($"[Player] OpenNewFile async: {filePath}");
        ThreadPool.QueueUserWorkItem(_ => OpenDecoderBackground());
        return true;
    }

    private void CleanupBeforeOpen()
    {
        _cancelOpen = true;
        _asyncOpenPending = false;
        _decodeRunning = false;
        _renderer.StopAudio();
        ClearQueues();
        lock (_videoLock) { Monitor.PulseAll(_videoLock); }
        lock (_audioLock) { Monitor.PulseAll(_audioLock); }
        if (_decodeThread != null && _decodeThread.IsAlive)
        {
            bool joined = _decodeThread.Join(3000);
            Console.Error.WriteLine($"[Player] DecodeThread joined={joined}");
        }

        // Wait for previous async open thread to see cancel and exit before disposing decoder
        var oldDecoder = _decoder;
        if (_asyncOpenRunning)
        {
            Console.Error.WriteLine("[Player] Waiting for previous async open to cancel...");
            for (int i = 0; i < 300 && _asyncOpenRunning; i++)
                Thread.Sleep(10);
            Console.Error.WriteLine($"[Player] Async open cancelled (running={_asyncOpenRunning})");
            // If still running after timeout, the thread is blocked in native FFmpeg call.
            // Replace decoder with a new instance — old one will be disposed when thread finishes.
            if (_asyncOpenRunning)
            {
                Console.Error.WriteLine("[Player] Async open still running, replacing decoder");
                _asyncOpenRunning = false;  // New thread will set this to true
            }
            else
            {
                try { oldDecoder.Dispose(); } catch { }
            }
        }
        else
        {
            try { oldDecoder.Dispose(); } catch { }
        }
        // Always create a fresh decoder for the new open
        _decoder = new FFmpegDecoder { UseHwAccel = _useHwAccel };
        Console.Error.WriteLine("[Player] Decoder disposed");

        _running = true;
        _trackEof = false;
        _paused = false;
        _clockStarted = false;
        _stopped = false;
        _trackEnded = false;
        _clock.Reset();
    }

    private void OpenDecoderBackground()
    {
        string filePath = _asyncOpenFilePath!;
        PlaylistEntry? entry = _asyncOpenEntry;
        var dec = _decoder;  // Capture current decoder instance
        string openUrl = filePath;  // May be replaced with link_url
        _asyncOpenRunning = true;
        try
        {
            if (_cancelOpen) { Console.Error.WriteLine("[Player] Open cancelled"); return; }
            dec.UseHwAccel = _useHwAccel;

            // For server stream URLs, try to resolve link_url first (external HLS plays directly)
            if (ServerClient != null && ServerClient.IsConnected && filePath.Contains("/api/media/"))
            {
                try
                {
                    var match = System.Text.RegularExpressions.Regex.Match(filePath, @"/api/media/(\d+)/stream");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int mediaId))
                    {
                        var media = ServerClient.GetMediaAsync(mediaId).Result;
                        if (media != null && !string.IsNullOrEmpty(media.LinkUrl))
                        {
                            openUrl = media.LinkUrl;
                            Console.Error.WriteLine($"[Player] Resolved link_url for media {mediaId}");
                            // Keep entry.Url as stream proxy URL (hidden from user)
                            _asyncOpenFilePath = openUrl;
                        }
                    }
                }
                catch (Exception exResolve)
                {
                    Console.Error.WriteLine($"[Player] link_url resolve failed: {exResolve.Message}");
                }
            }

            dec.Open(openUrl);
            // If decoder was replaced during open, abort
            if (!ReferenceEquals(_decoder, dec))
            {
                Console.Error.WriteLine("[Player] Decoder replaced during open, aborting");
                dec.Dispose();
                return;
            }
            Console.Error.WriteLine($"[Player] Opened: video={dec.VideoStreamIndex} audio={dec.AudioStreamIndex} dur={dec.DurationSec:F1}s");
            _asyncOpenSuccess = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Player] Open failed ({openUrl}): {ex.Message}");

            if (_cancelOpen || !ReferenceEquals(_decoder, dec))
            {
                Console.Error.WriteLine("[Player] Open cancelled after failure");
                try { dec.Dispose(); } catch { }
                return;
            }

            // Fallback 1: If link_url failed, try stream proxy URL (server handles headers/UA)
            if (openUrl != filePath && filePath.Contains("/api/media/"))
            {
                try
                {
                    Console.Error.WriteLine($"[Player] Falling back to stream proxy: {filePath}");
                    dec.Open(filePath);
                    if (!ReferenceEquals(_decoder, dec))
                    {
                        Console.Error.WriteLine("[Player] Decoder replaced during fallback, aborting");
                        dec.Dispose();
                        return;
                    }
                    Console.Error.WriteLine($"[Player] Fallback opened: video={dec.VideoStreamIndex} audio={dec.AudioStreamIndex} dur={dec.DurationSec:F1}s");
                    _asyncOpenSuccess = true;
                    _asyncOpenFilePath = filePath;
                    return;
                }
                catch (Exception exFallback)
                {
                    Console.Error.WriteLine($"[Player] Stream proxy fallback failed: {exFallback.Message}");
                    if (_cancelOpen || !ReferenceEquals(_decoder, dec))
                    {
                        Console.Error.WriteLine("[Player] Fallback cancelled after failure");
                        try { dec.Dispose(); } catch { }
                        return;
                    }
                }
            }

            // Fallback 2: Re-extract from filman.cc SourceUrl
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
                            dec.Open(fresh.Url);
                            Console.Error.WriteLine($"[Player] Re-opened: video={dec.VideoStreamIndex} audio={dec.AudioStreamIndex} dur={dec.DurationSec:F1}s");
                            _asyncOpenSuccess = true;
                            _asyncOpenFilePath = fresh.Url;
                            return;
                        }
                    }
                    catch (Exception ex2)
                    {
                        Console.Error.WriteLine($"[Player] Re-extraction failed: {ex2.Message}");
                    }
                }
            }
            _asyncOpenError = ex;
        }
        finally
        {
            // Only signal completion if our decoder is still the active one.
            // If decoder was replaced, the main thread should NOT process our result.
            if (ReferenceEquals(_decoder, dec))
            {
                _asyncOpenComplete = true;
                _asyncOpenPending = false;
                _asyncOpenRunning = false;
            }
        }
    }

    public void ProcessAsyncOpenCompletion()
    {
        if (!_asyncOpenComplete) return;
        _asyncOpenComplete = false;

        if (_asyncOpenSuccess)
        {
            // Clear any pending retry — the open succeeded, no need to re-queue
            _pendingOpenFile = null;
            _pendingOpenEntry = null;
            FinishOpen(_asyncOpenFilePath!, _asyncOpenEntry);
        }
        else
            HandleOpenFailure(_asyncOpenFilePath!, _asyncOpenEntry, _asyncOpenAttempt, _asyncOpenError);
    }

    private void FinishOpen(string filePath, PlaylistEntry? entry)
    {
        // Guard: ensure decoder was actually opened successfully
        if (_decoder.VideoStreamIndex < 0 && _decoder.AudioStreamIndex < 0)
        {
            Console.Error.WriteLine("[Player] FinishOpen: decoder has no streams, aborting");
            _trackEnded = true;
            return;
        }

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

        _decodeRunning = true;
        _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "Decode" };
        _decodeThread.Start();
        _trackOpenTicks = Environment.TickCount64;
        Console.Error.WriteLine($"[Player] DecodeThread started");

        if (StartPositionSec > 1.0)
        {
            Console.Error.WriteLine($"[Player] Restoring position {StartPositionSec:F1}s");
            Thread.Sleep(300);
            RequestSeek(StartPositionSec);
            StartPositionSec = 0;
        }
    }

    private void HandleOpenFailure(string filePath, PlaylistEntry? entry, int attempt, Exception? error)
    {
        if (attempt < Playlist?.Count && Playlist != null && Playlist.Count > 1)
        {
            bool advanced = Playlist.AdvanceToNext();
            if (advanced)
            {
                Console.Error.WriteLine($"[Player] Open failed, trying next playlist item: {Playlist.Current}");
                OpenNewFile(Playlist.Current, Playlist.CurrentEntry, attempt + 1);
                return;
            }
        }

        _trackEnded = true;
        _decodeRunning = false;
        _renderer.ShowError($"Błąd odtwarzania:\n{Path.GetFileName(filePath)}\n{error?.Message}");
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
        lock (Playlist)
        {
            if (Playlist.Count == 0)
            {
                _renderer.SetPlaylistData(Array.Empty<string>(), Array.Empty<double>(), -1, 0);
                return;
            }
            int currentIndex = Playlist.CurrentIndex;
            var entries = Playlist.Entries.ToArray();
            var names = new string[entries.Length];
            var durations = new double[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                names[i] = entries[i].DisplayName;
                durations[i] = i == currentIndex ? _duration : 0;
            }
            _renderer.SetPlaylistData(names, durations, currentIndex, GetMasterClock());
        }
    }

    // Cached playlist list for series expansion
    private List<RemotePlaylist> _serverPlaylistListCache = new();

    private void BuildServerPlaylistList(List<RemotePlaylist> playlists)
    {
        _serverPlaylistListCache = playlists;

        // Categorize: "Filmy" = non-series, "Seriale" = contains "—" (series version playlists)
        var moviePlaylists = new List<(RemotePlaylist pl, List<RemoteMedia> items)>();
        var seriesPlaylists = new List<(RemotePlaylist pl, List<RemoteMedia> items)>();

        foreach (var pl in playlists)
        {
            var detail = ServerClient.GetPlaylistAsync(pl.Id).Result;
            int count = detail?.Items.Count ?? 0;
            if (count == 0) continue;

            // Series playlists have "—" in name (e.g. "Arcane — Dubbing")
            if (pl.Name.Contains("—") || pl.Name.Contains(" - "))
                seriesPlaylists.Add((pl, detail.Items));
            else
                moviePlaylists.Add((pl, detail.Items));
        }

        // Count distinct movie titles (same movie on multiple sources = 1)
        int distinctMovies = moviePlaylists
            .SelectMany(mp => mp.items)
            .Select(m => m.Title?.Trim() ?? "")
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .Count();

        // Count distinct series names (part before "—")
        int distinctSeries = seriesPlaylists
            .Select(sp => sp.pl.Name.Split("—")[0].Split(" - ")[0].Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .Count();

        lock (Playlist!)
        {
            Playlist.Clear();
            // Movies first
            foreach (var (pl, items) in moviePlaylists)
            {
                Playlist.Add(new PlaylistEntry(
                    $"serverplaylist://{pl.Id}",
                    $"🎬 {pl.Name} ({distinctMovies})",
                    null));
            }
            // Series category
            if (seriesPlaylists.Count > 0)
            {
                Playlist.Add(new PlaylistEntry(
                    "serverseries://",
                    $"📺 Seriale ({distinctSeries})",
                    null));
            }
        }
        _renderer.PlaylistSelectedIndex = 0;
        _forceRedraw = true;

        _renderer.ShowStatus($"Załadowano {playlists.Count} playlist z serwera");
        _forceRedraw = true;
        System.Threading.Thread.Sleep(1500);
        _renderer.ClearStatus();
        _forceRedraw = true;
    }

    private void LoadServerSeriesList()
    {
        // Show series playlists from cache
        var seriesPlaylists = _serverPlaylistListCache
            .Where(pl => pl.Name.Contains("—") || pl.Name.Contains(" - "))
            .ToList();

        lock (Playlist!)
        {
            Playlist.Clear();
            Playlist.Add(new PlaylistEntry("serverback://", "← Wstecz do playlist", null));
            foreach (var pl in seriesPlaylists)
            {
                var detail = ServerClient.GetPlaylistAsync(pl.Id).Result;
                int count = detail?.Items.Count ?? 0;
                if (count == 0) continue;
                Playlist.Add(new PlaylistEntry(
                    $"serverplaylist://{pl.Id}",
                    $"📺 {pl.Name} ({count})",
                    null));
            }
            Playlist.Reset();
        }
        _renderer.PlaylistSelectedIndex = 1; // Skip back button
        _forceRedraw = true;

        _renderer.ShowStatus($"Seriale — {seriesPlaylists.Count} playlist");
        _forceRedraw = true;
        System.Threading.Thread.Sleep(1500);
        _renderer.ClearStatus();
        _forceRedraw = true;
    }

    private void LoadServerPlaylist(int playlistId)
    {
        if (ServerClient == null || !ServerClient.IsConnected) return;

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _renderer.ShowStatus("\u21BB Ładowanie playlisty z serwera...");
                _forceRedraw = true;

                var detail = ServerClient.GetPlaylistAsync(playlistId).Result;
                if (detail == null || detail.Items.Count == 0)
                {
                    _renderer.ShowStatus("Pusta playlista");
                    _forceRedraw = true;
                    System.Threading.Thread.Sleep(1500);
                    _renderer.ClearStatus();
                    _forceRedraw = true;
                    return;
                }

                // Cache items for later navigation
                ServerPlaylistCache[playlistId] = detail.Items;

                // Check if items have season info (series) or not (movie)
                var seasons = detail.Items
                    .Where(m => m.SeasonNumber.HasValue)
                    .Select(m => m.SeasonNumber!.Value)
                    .Distinct()
                    .OrderBy(s => s)
                    .ToList();

                if (seasons.Count > 0)
                {
                    // Series: fetch detailed info and show info panel
                    string seriesName = detail.Items.FirstOrDefault(m => !string.IsNullOrEmpty(m.SeriesName))?.SeriesName ?? detail.Name;
                    SeriesDetail? seriesDetail = null;
                    try
                    {
                        seriesDetail = ServerClient.GetSeriesDetailAsync(seriesName).Result;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Server] Series detail error: {ex.Message}");
                    }

                    // Try to fetch rich info from filman.cc
                    SeriesInfo? seriesInfo = null;
                    try
                    {
                        string filmanUrl = $"https://filman.cc/s/{seriesName.ToLower().Replace(' ', '-')}";
                        seriesInfo = ServerClient.GetSeriesInfoAsync(filmanUrl).Result;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Server] SeriesInfo fetch error: {ex.Message}");
                    }

                    // Show seasons in playlist with back button
                    lock (Playlist!)
                    {
                        Playlist.Clear();
                        Playlist.Add(new PlaylistEntry("serverback://", "← Wstecz do playlist", null));
                        foreach (var s in seasons)
                        {
                            var epCount = detail.Items.Count(m => m.SeasonNumber == s);
                            Playlist.Add(new PlaylistEntry(
                                $"serverseason://{playlistId}:{s}",
                                $"🎬 Sezon {s} ({epCount} odc.)",
                                null));
                        }
                    }
                    _renderer.PlaylistSelectedIndex = 1; // Skip back button
                    _forceRedraw = true;

                    // Show series info panel in video area
                    if (seriesInfo != null && !string.IsNullOrEmpty(seriesInfo.Title))
                    {
                        _renderer.SeriesInfoName = seriesInfo.Title;
                        _renderer.SeriesInfoDescription = seriesInfo.Description ?? "";
                        _renderer.SeriesInfoTotal = $"Łącznie: {(seriesDetail?.TotalLinks ?? detail.Items.Count)} linków";
                        _renderer.SeriesInfoVersions = string.Join(", ", seriesDetail?.Versions ?? new List<string>());
                        var seasonLines = (seriesDetail?.Seasons ?? new List<SeriesSeason>()).Select(s =>
                            $"Sezon {s.Season}: {s.EpisodeCount} odcinków, {s.LinkCount} linków");
                        _renderer.SeriesInfoSeasons = string.Join("\n", seasonLines);
                        _renderer.SeriesInfoPosterUrl = seriesInfo.Poster;
                        _renderer.SeriesInfoRating = seriesInfo.Rating;
                        _renderer.SeriesInfoVotes = seriesInfo.Votes;
                        _renderer.SeriesInfoImdbRating = seriesInfo.ImdbRating;
                        _renderer.SeriesInfoImdbVotes = seriesInfo.ImdbVotes;
                        _renderer.SeriesInfoYear = seriesInfo.Year;
                        _renderer.SeriesInfoViews = seriesInfo.Views;
                        _renderer.SeriesInfoDurationText = seriesInfo.DurationText;
                        _renderer.SeriesInfoCountries = seriesInfo.Countries ?? new List<string>();
                        _renderer.SeriesInfoGenres = seriesInfo.Genres ?? new List<string>();
                        if (!string.IsNullOrEmpty(seriesInfo.Poster))
                            _renderer.LoadSeriesPoster(seriesInfo.Poster);
                    }
                    else if (seriesDetail != null)
                    {
                        _renderer.SeriesInfoName = seriesDetail.SeriesName;
                        _renderer.SeriesInfoDescription = "";
                        _renderer.SeriesInfoTotal = $"Łącznie: {seriesDetail.TotalLinks} linków";
                        _renderer.SeriesInfoVersions = string.Join(", ", seriesDetail.Versions);
                        var seasonLines = seriesDetail.Seasons.Select(s =>
                            $"Sezon {s.Season}: {s.EpisodeCount} odcinków, {s.LinkCount} linków");
                        _renderer.SeriesInfoSeasons = string.Join("\n", seasonLines);
                    }
                    else
                    {
                        _renderer.SeriesInfoName = detail.Name;
                        _renderer.SeriesInfoDescription = "";
                        _renderer.SeriesInfoTotal = $"Łącznie: {detail.Items.Count} pozycji";
                        _renderer.SeriesInfoVersions = string.Join(", ", detail.Items.Select(m => m.Version ?? "").Distinct());
                        _renderer.SeriesInfoSeasons = string.Join("\n", seasons.Select(s => $"Sezon {s}: {detail.Items.Count(m => m.SeasonNumber == s)} pozycji"));
                    }
                    _renderer.SeriesInfoVisible = true;
                    _forceRedraw = true;

                    _renderer.ShowStatus($"\"{detail.Name}\" — {seasons.Count} sezon(ów)");
                    _forceRedraw = true;
                    System.Threading.Thread.Sleep(1500);
                    _renderer.ClearStatus();
                    _forceRedraw = true;
                }
                else
                {
                    // Movie or non-series: check for multiple sources
                    var sources = detail.Items
                        .Where(m => !string.IsNullOrEmpty(m.SourceName))
                        .Select(m => m.SourceName!)
                        .Distinct()
                        .OrderBy(s => s)
                        .ToList();

                    if (sources.Count > 1)
                    {
                        // Multiple sources — show source selection
                        ServerPlaylistCache[playlistId] = detail.Items;
                        lock (Playlist!)
                        {
                            Playlist.Clear();
                            Playlist.Add(new PlaylistEntry("serverback://", "← Wstecz do playlist", null));
                            foreach (var src in sources)
                            {
                                var count = detail.Items.Count(m => m.SourceName == src);
                                Playlist.Add(new PlaylistEntry(
                                    $"servermoviesource://{playlistId}:{Uri.EscapeDataString(src)}",
                                    $"🌐 {src} ({count} pozycji)",
                                    null));
                            }
                            Playlist.Reset();
                        }

                        _renderer.PlaylistSelectedIndex = 1;
                        _forceRedraw = true;

                        _renderer.ShowStatus($"Wybierz źródło ({sources.Count} dostępnych)");
                        _forceRedraw = true;
                        System.Threading.Thread.Sleep(1500);
                        _renderer.ClearStatus();
                        _forceRedraw = true;
                    }
                    else
                    {
                        // Single or no source — play directly
                        lock (Playlist!)
                        {
                            Playlist.Clear();
                            foreach (var m in detail.Items)
                            {
                                string playUrl = ServerClient.GetStreamUrl(m.Id);
                                Playlist.Add(new PlaylistEntry(playUrl, m.DisplayName, null));
                            }
                            Playlist.Reset();
                        }

                        _renderer.PlaylistSelectedIndex = 0;
                        _forceRedraw = true;

                        _renderer.ShowStatus($"Załadowano \"{detail.Name}\" ({detail.Items.Count} pozycji)");
                        _forceRedraw = true;
                        System.Threading.Thread.Sleep(1500);
                        _renderer.ClearStatus();
                        _forceRedraw = true;

                        // Auto-play first item
                        if (Playlist != null && Playlist.Count > 0)
                        {
                            _pendingOpenFile = Playlist.Current;
                            _pendingOpenEntry = Playlist.CurrentEntry;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Server] LoadPlaylist error: {ex.Message}");
                _renderer.ShowError($"Błąd ładowania playlisty: {ex.Message}");
                _forceRedraw = true;
            }
        });
    }

    private void LoadServerSeason(int playlistId, int seasonNum)
    {
        if (!ServerPlaylistCache.TryGetValue(playlistId, out var allItems))
        {
            // Cache miss — fetch and retry
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _renderer.ShowStatus("\u21BB Ładowanie sezonu...");
                    _forceRedraw = true;

                    var detail = ServerClient.GetPlaylistAsync(playlistId).Result;
                    if (detail == null || detail.Items.Count == 0) return;

                    ServerPlaylistCache[playlistId] = detail.Items;
                    LoadServerSeason(playlistId, seasonNum);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Server] LoadSeason error: {ex.Message}");
                    _renderer.ShowError($"Błąd: {ex.Message}");
                    _forceRedraw = true;
                }
            });
            return;
        }

        var seasonItems = allItems
            .Where(m => m.SeasonNumber == seasonNum)
            .ToList();

        if (seasonItems.Count == 0) return;

        // Get distinct sources for this season
        var sources = seasonItems
            .Where(m => !string.IsNullOrEmpty(m.SourceName))
            .Select(m => m.SourceName!)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        if (sources.Count <= 1)
        {
            // Only one source — skip source selection, load episodes directly
            LoadServerSource(playlistId, seasonNum, sources.Count == 1 ? sources[0] : "");
            return;
        }

        // Show source selection
        lock (Playlist!)
        {
            Playlist.Clear();
            Playlist.Add(new PlaylistEntry("serverback://", "← Wstecz do sezonów", null));
            foreach (var src in sources)
            {
                var count = seasonItems.Count(m => m.SourceName == src);
                Playlist.Add(new PlaylistEntry(
                    $"serversource://{playlistId}:{seasonNum}:{Uri.EscapeDataString(src)}",
                    $"🌐 {src} ({count} pozycji)",
                    null));
            }
        }

        _renderer.PlaylistSelectedIndex = 1; // Skip back button
        _renderer.SeriesInfoVisible = false;
        _forceRedraw = true;

        _renderer.ShowStatus($"Sezon {seasonNum} — wybierz źródło ({sources.Count} dostępnych)");
        _forceRedraw = true;
        System.Threading.Thread.Sleep(1500);
        _renderer.ClearStatus();
        _forceRedraw = true;
    }

    private void LoadServerSource(int playlistId, int seasonNum, string sourceName)
    {
        if (!ServerPlaylistCache.TryGetValue(playlistId, out var allItems))
        {
            Console.Error.WriteLine($"[Server] LoadSource: cache miss for playlist {playlistId}");
            return;
        }

        sourceName = Uri.UnescapeDataString(sourceName);

        var episodes = allItems
            .Where(m => m.SeasonNumber == seasonNum && (string.IsNullOrEmpty(sourceName) || m.SourceName == sourceName))
            .OrderBy(m => m.EpisodeNumber ?? 0)
            .ToList();

        if (episodes.Count == 0) return;

        lock (Playlist!)
        {
            Playlist.Clear();
            Playlist.Add(new PlaylistEntry("serverback://", "← Wstecz do źródeł", null));
            foreach (var m in episodes)
            {
                string playUrl = ServerClient.GetStreamUrl(m.Id);
                string display = m.EpisodeNumber.HasValue
                    ? $"S{seasonNum:D2}E{m.EpisodeNumber:D2} — {m.EpisodeTitle ?? m.Title}"
                    : m.DisplayName;
                if (!string.IsNullOrEmpty(m.Version) && m.Version != "Unknown")
                    display += $" [{m.Version}]";
                Playlist.Add(new PlaylistEntry(playUrl, display, null));
            }
            Playlist.Reset();
        }

        _renderer.PlaylistSelectedIndex = 1; // Skip back button
        _renderer.SeriesInfoVisible = false;
        _forceRedraw = true;

        string srcLabel = string.IsNullOrEmpty(sourceName) ? "wszystkie" : sourceName;
        _renderer.ShowStatus($"Sezon {seasonNum} — {srcLabel} — {episodes.Count} odcinków");
        _forceRedraw = true;
        System.Threading.Thread.Sleep(1500);
        _renderer.ClearStatus();
        _forceRedraw = true;

        // Auto-play first item
        if (Playlist != null && Playlist.Count > 0)
        {
            _pendingOpenFile = Playlist.Current;
            _pendingOpenEntry = Playlist.CurrentEntry;
        }
    }

    private void LoadServerMovieSource(int playlistId, string sourceName)
    {
        if (!ServerPlaylistCache.TryGetValue(playlistId, out var allItems))
        {
            Console.Error.WriteLine($"[Server] LoadMovieSource: cache miss for playlist {playlistId}");
            return;
        }

        sourceName = Uri.UnescapeDataString(sourceName);

        var movies = allItems
            .Where(m => string.IsNullOrEmpty(sourceName) || m.SourceName == sourceName)
            .OrderBy(m => m.Title)
            .ToList();

        if (movies.Count == 0) return;

        lock (Playlist!)
        {
            Playlist.Clear();
            Playlist.Add(new PlaylistEntry("serverback://", "← Wstecz do źródeł", null));
            foreach (var m in movies)
            {
                string playUrl = ServerClient.GetStreamUrl(m.Id);
                Playlist.Add(new PlaylistEntry(playUrl, m.DisplayName, null));
            }
            Playlist.Reset();
        }

        _renderer.PlaylistSelectedIndex = 1;
        _forceRedraw = true;

        _renderer.ShowStatus($"Filmy — {sourceName} — {movies.Count} pozycji");
        _forceRedraw = true;
        System.Threading.Thread.Sleep(1500);
        _renderer.ClearStatus();
        _forceRedraw = true;

        // Auto-play first item
        if (Playlist != null && Playlist.Count > 0)
        {
            _pendingOpenFile = Playlist.Current;
            _pendingOpenEntry = Playlist.CurrentEntry;
        }
    }

    private void LoadServerYear(string year)
    {
        if (ServerClient == null || !ServerClient.IsConnected) return;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _renderer.ShowStatus($"↻ Pobieranie pozycji z roku {year}...");
                _forceRedraw = true;

                var items = ServerClient.ListMediaFilteredAsync(500, 0, year: year).Result;
                if (items.Count == 0)
                {
                    _renderer.ShowStatus($"Brak pozycji dla roku {year}");
                    _forceRedraw = true;
                    System.Threading.Thread.Sleep(2000);
                    _renderer.ClearStatus();
                    _forceRedraw = true;
                    return;
                }

                lock (Playlist!)
                {
                    Playlist.Clear();
                    Playlist.Add(new PlaylistEntry("serverback://", "← Wstecz do lat", null));
                    foreach (var m in items)
                    {
                        string playUrl = ServerClient.GetStreamUrl(m.Id);
                        string display = m.DisplayName;
                        if (!string.IsNullOrEmpty(m.Version) && m.Version != "Unknown")
                            display += $" [{m.Version}]";
                        if (!string.IsNullOrEmpty(m.SourceName))
                            display += $" ({m.SourceName})";
                        Playlist.Add(new PlaylistEntry(playUrl, display, null));
                    }
                    Playlist.Reset();
                }

                _renderer.PlaylistSelectedIndex = 1;
                _renderer.SeriesInfoVisible = false;
                _forceRedraw = true;
                _renderer.ShowStatus($"Rok {year} — {items.Count} pozycji");
                _forceRedraw = true;
                System.Threading.Thread.Sleep(1500);
                _renderer.ClearStatus();
                _forceRedraw = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Server] LoadYear error: {ex.Message}");
                _renderer.ShowError($"Błąd: {ex.Message}");
                _forceRedraw = true;
            }
        });
    }

    private void LoadServerMediaType(string mediaType)
    {
        if (ServerClient == null || !ServerClient.IsConnected) return;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _renderer.ShowStatus($"↻ Pobieranie pozycji typu {mediaType}...");
                _forceRedraw = true;

                var items = ServerClient.ListMediaAsync(500, 0, mediaType: mediaType).Result;
                if (items.Count == 0)
                {
                    _renderer.ShowStatus($"Brak pozycji dla typu {mediaType}");
                    _forceRedraw = true;
                    System.Threading.Thread.Sleep(2000);
                    _renderer.ClearStatus();
                    _forceRedraw = true;
                    return;
                }

                lock (Playlist!)
                {
                    Playlist.Clear();
                    Playlist.Add(new PlaylistEntry("serverback://", "← Wstecz do filtrów", null));
                    foreach (var m in items)
                    {
                        string playUrl = ServerClient.GetStreamUrl(m.Id);
                        string display = m.DisplayName;
                        if (!string.IsNullOrEmpty(m.Version) && m.Version != "Unknown")
                            display += $" [{m.Version}]";
                        if (!string.IsNullOrEmpty(m.SourceName))
                            display += $" ({m.SourceName})";
                        Playlist.Add(new PlaylistEntry(playUrl, display, null));
                    }
                    Playlist.Reset();
                }

                _renderer.PlaylistSelectedIndex = 1;
                _renderer.SeriesInfoVisible = false;
                _forceRedraw = true;
                _renderer.ShowStatus($"Typ {mediaType} — {items.Count} pozycji");
                _forceRedraw = true;
                System.Threading.Thread.Sleep(1500);
                _renderer.ClearStatus();
                _forceRedraw = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Server] LoadMediaType error: {ex.Message}");
                _renderer.ShowError($"Błąd: {ex.Message}");
                _forceRedraw = true;
            }
        });
    }

    private void TryShowEpisodeInfo()
    {
        if (Playlist == null || _renderer.PlaylistSelectedIndex < 0) return;
        int idx = _renderer.PlaylistSelectedIndex;
        if (idx >= Playlist.Count) return;

        var entry = Playlist.Entries[idx];
        if (entry == null) return;

        // Only show for actual episode entries (not navigation entries)
        string url = entry.Url ?? "";
        if (url.StartsWith("server") || url == "separator://") return;

        // Find the media item in cache to get series name and episode number
        RemoteMedia? mediaItem = null;
        // Extract media ID from stream proxy URL for matching
        int urlMediaId = -1;
        var idMatch = System.Text.RegularExpressions.Regex.Match(url, @"/api/media/(\d+)/stream");
        if (idMatch.Success && int.TryParse(idMatch.Groups[1].Value, out int mid))
            urlMediaId = mid;

        foreach (var kvp in ServerPlaylistCache)
        {
            mediaItem = kvp.Value.FirstOrDefault(m =>
                (urlMediaId > 0 && m.Id == urlMediaId) ||
                (!string.IsNullOrEmpty(m.LinkUrl) && m.LinkUrl == url) ||
                m.Title == entry.DisplayName);
            if (mediaItem != null) break;
        }

        if (mediaItem == null) return;

        // Build filman.cc episode URL from series name and season/episode
        string seriesName = mediaItem.SeriesName ?? "";
        int? season = mediaItem.SeasonNumber;
        int? episode = mediaItem.EpisodeNumber;

        if (string.IsNullOrEmpty(seriesName) || !season.HasValue || !episode.HasValue)
        {
            // Show basic info without filman.cc lookup
            _renderer.EpisodeInfoTitle = mediaItem.EpisodeTitle ?? mediaItem.Title ?? entry.DisplayName;
            _renderer.EpisodeInfoDescription = mediaItem.Description ?? "";
            _renderer.EpisodeInfoSeasonEp = $"S{season?.ToString("D2") ?? "01"}E{episode?.ToString("D2") ?? "01"}";
            _renderer.EpisodeInfoSources = mediaItem.SourceName ?? "";
            _renderer.EpisodeInfoVisible = true;
            return;
        }

        // Try filman.cc URL
        string filmanUrl = $"https://filman.cc/e/{seriesName.ToLower().Replace(' ', '-')}/sezon-{season}/odcinek-{episode}";

        // Fetch async to avoid blocking UI
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var info = ServerClient.GetEpisodeInfoAsync(filmanUrl).Result;
                if (info != null)
                {
                    _renderer.EpisodeInfoTitle = info.Title ?? mediaItem.EpisodeTitle ?? mediaItem.Title ?? entry.DisplayName;
                    _renderer.EpisodeInfoDescription = info.Description ?? "";
                    _renderer.EpisodeInfoSeasonEp = $"S{season:D2}E{episode:D2}";
                    _renderer.EpisodeInfoSources = string.Join(", ", info.AvailableSources.Select(s => $"{s.Source} ({s.Version})"));
                }
                else
                {
                    _renderer.EpisodeInfoTitle = mediaItem.EpisodeTitle ?? mediaItem.Title ?? entry.DisplayName;
                    _renderer.EpisodeInfoDescription = mediaItem.Description ?? "";
                    _renderer.EpisodeInfoSeasonEp = $"S{season:D2}E{episode:D2}";
                    _renderer.EpisodeInfoSources = mediaItem.SourceName ?? "";
                }
                _renderer.EpisodeInfoVisible = true;
                _forceRedraw = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Episode] Info fetch error: {ex.Message}");
                _renderer.EpisodeInfoTitle = mediaItem.EpisodeTitle ?? mediaItem.Title ?? entry.DisplayName;
                _renderer.EpisodeInfoDescription = mediaItem.Description ?? "";
                _renderer.EpisodeInfoSeasonEp = $"S{season:D2}E{episode:D2}";
                _renderer.EpisodeInfoSources = mediaItem.SourceName ?? "";
                _renderer.EpisodeInfoVisible = true;
                _forceRedraw = true;
            }
        });
    }
}
