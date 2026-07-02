using System.Net.Http;
using System.Runtime.InteropServices;
using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    [DllImport("SDL2_image", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr IMG_Load_RW(IntPtr src, int freesrc);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_RWFromMem(byte[] mem, int size);
    public bool SeriesInfoVisible = false;
    public string SeriesInfoName = "";
    public string SeriesInfoDescription = "";
    public string SeriesInfoVersions = "";
    public string SeriesInfoSeasons = "";
    public string SeriesInfoTotal = "";
    public string? SeriesInfoPosterUrl;
    public string? SeriesInfoRating;
    public string? SeriesInfoVotes;
    public string? SeriesInfoImdbRating;
    public string? SeriesInfoImdbVotes;
    public string? SeriesInfoYear;
    public string? SeriesInfoViews;
    public string? SeriesInfoDurationText;
    public List<string> SeriesInfoCountries = new();
    public List<string> SeriesInfoGenres = new();

    private IntPtr _seriesPosterTexture = IntPtr.Zero;
    private int _seriesPosterW, _seriesPosterH;
    private string? _seriesPosterLoadingUrl;

    private static readonly HttpClient _posterHttpClient = new HttpClient();

    public void LoadSeriesPoster(string url)
    {
        if (_seriesPosterLoadingUrl == url) return;
        _seriesPosterLoadingUrl = url;

        if (_seriesPosterTexture != IntPtr.Zero)
        {
            SDL.SDL_DestroyTexture(_seriesPosterTexture);
            _seriesPosterTexture = IntPtr.Zero;
        }

        if (string.IsNullOrEmpty(url)) return;

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36");
                var resp = _posterHttpClient.Send(req);
                if (!resp.IsSuccessStatusCode) return;
                using var ms = new System.IO.MemoryStream();
                resp.Content.ReadAsStream().CopyTo(ms);
                var data = ms.ToArray();

                // Load as SDL surface via SDL_image (RWops + IMG_Load)
                var rw = SDL_RWFromMem(data, data.Length);
                if (rw == IntPtr.Zero) return;
                var surface = IMG_Load_RW(rw, 1);
                if (surface == IntPtr.Zero) return;

                // Convert to texture on main thread — store surface ptr for deferred conversion
                // Actually we need to create texture on the renderer thread, so store the data
                lock (_posterLock)
                {
                    _pendingPosterSurface = surface;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Poster] Load failed: {ex.Message}");
            }
        });
    }

    private readonly object _posterLock = new();
    private IntPtr _pendingPosterSurface = IntPtr.Zero;

    private void ApplyPendingPoster()
    {
        IntPtr surfaceToProcess = IntPtr.Zero;
        lock (_posterLock)
        {
            if (_pendingPosterSurface != IntPtr.Zero)
            {
                surfaceToProcess = _pendingPosterSurface;
                _pendingPosterSurface = IntPtr.Zero;
            }
        }
        if (surfaceToProcess != IntPtr.Zero)
        {
            if (_seriesPosterTexture != IntPtr.Zero)
            {
                SDL.SDL_DestroyTexture(_seriesPosterTexture);
                _seriesPosterTexture = IntPtr.Zero;
            }
            _seriesPosterTexture = SDL.SDL_CreateTextureFromSurface(_renderer, surfaceToProcess);
            if (_seriesPosterTexture != IntPtr.Zero)
            {
                SDL.SDL_QueryTexture(_seriesPosterTexture, out _, out _, out _seriesPosterW, out _seriesPosterH);
            }
            SDL.SDL_FreeSurface(surfaceToProcess);
        }
    }

    public void ClearSeriesPoster()
    {
        if (_seriesPosterTexture != IntPtr.Zero)
        {
            SDL.SDL_DestroyTexture(_seriesPosterTexture);
            _seriesPosterTexture = IntPtr.Zero;
        }
        _seriesPosterLoadingUrl = null;
    }

    private void DrawSeriesInfo()
    {
        if (_font == IntPtr.Zero) return;

        ApplyPendingPoster();

        int panW = 720, panH = 420;
        int panX = (_windowW - panW) / 2;
        int panY = (_windowH - panH) / 2;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 140);
        var shadow = new SDL.SDL_Rect { x = panX + 6, y = panY + 6, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref shadow);

        SDL.SDL_SetRenderDrawColor(_renderer, 28, 30, 38, 250);
        var bg = new SDL.SDL_Rect { x = panX, y = panY, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref bg);

        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 255, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref bg);

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);

        var white = new SDL.SDL_Color { r = 230, g = 230, b = 230, a = 255 };
        var blue  = new SDL.SDL_Color { r = 80,  g = 160, b = 255, a = 255 };
        var gray  = new SDL.SDL_Color { r = 150, g = 150, b = 150, a = 255 };
        var green = new SDL.SDL_Color { r = 100, g = 220, b = 130, a = 255 };
        var muted = new SDL.SDL_Color { r = 110, g = 110, b = 110, a = 255 };
        var gold  = new SDL.SDL_Color { r = 255, g = 193, b = 7, a = 255 };
        var orange = new SDL.SDL_Color { r = 244, g = 165, b = 34, a = 255 };

        // Layout: poster on left, text on right
        int posterAreaW = 200;
        int textX = panX + posterAreaW + 20;
        int textW = panW - posterAreaW - 50;
        int lineH = 26;

        // Draw poster
        if (_seriesPosterTexture != IntPtr.Zero)
        {
            // Fit poster in posterAreaW x 280 area, maintaining aspect ratio
            int maxPosterW = posterAreaW - 20;
            int maxPosterH = 280;
            double aspect = (double)_seriesPosterW / _seriesPosterH;
            int pw, ph;
            if (maxPosterW / aspect <= maxPosterH)
            {
                pw = maxPosterW;
                ph = (int)(maxPosterW / aspect);
            }
            else
            {
                ph = maxPosterH;
                pw = (int)(maxPosterH * aspect);
            }
            int px = panX + 10 + (maxPosterW - pw) / 2;
            int py = panY + 20;
            var dst = new SDL.SDL_Rect { x = px, y = py, w = pw, h = ph };
            SDL.SDL_RenderCopy(_renderer, _seriesPosterTexture, IntPtr.Zero, ref dst);
        }
        else
        {
            // Placeholder
            var ph = new SDL.SDL_Rect { x = panX + 10, y = panY + 20, w = posterAreaW - 20, h = 280 };
            SDL.SDL_SetRenderDrawColor(_renderer, 40, 42, 50, 255);
            SDL.SDL_RenderFillRect(_renderer, ref ph);
            SDL.SDL_SetRenderDrawColor(_renderer, 60, 62, 70, 255);
            SDL.SDL_RenderDrawRect(_renderer, ref ph);
            var placeholderColor = new SDL.SDL_Color { r = 80, g = 80, b = 80, a = 255 };
            RenderText("Brak plakatu", panX + 40, panY + 140, placeholderColor);
        }

        // Ratings below poster
        int ratingY = panY + 310;
        if (!string.IsNullOrEmpty(SeriesInfoRating))
        {
            RenderText($"★ {SeriesInfoRating}/5", panX + 10, ratingY, gold);
            if (!string.IsNullOrEmpty(SeriesInfoVotes))
                RenderText($"({SeriesInfoVotes} głosów)", panX + 10, ratingY + 22, muted);
            ratingY += 48;
        }
        if (!string.IsNullOrEmpty(SeriesInfoImdbRating))
        {
            RenderText($"IMDb: {SeriesInfoImdbRating}/10", panX + 10, ratingY, orange);
            if (!string.IsNullOrEmpty(SeriesInfoImdbVotes))
                RenderText($"({SeriesInfoImdbVotes} ocen)", panX + 10, ratingY + 22, muted);
        }

        // Text area on the right
        int x = textX;
        int y = panY + 24;

        RenderText(SeriesInfoName, x, y, blue);
        y += lineH + 4;

        if (!string.IsNullOrEmpty(SeriesInfoYear))
        {
            var metaParts = new List<string> { SeriesInfoYear };
            if (!string.IsNullOrEmpty(SeriesInfoDurationText))
                metaParts.Add(SeriesInfoDurationText);
            if (SeriesInfoCountries.Count > 0)
                metaParts.Add(string.Join(", ", SeriesInfoCountries));
            RenderText(string.Join("  |  ", metaParts), x, y, gray);
            y += lineH;
        }

        if (!string.IsNullOrEmpty(SeriesInfoViews))
        {
            RenderText($"Wyświetlenia: {SeriesInfoViews}", x, y, muted);
            y += lineH;
        }

        if (SeriesInfoGenres.Count > 0)
        {
            RenderText("Gatunki:", x, y, gray);
            RenderText(string.Join(", ", SeriesInfoGenres), x + 90, y, white);
            y += lineH;
        }

        if (!string.IsNullOrEmpty(SeriesInfoTotal))
        {
            RenderText(SeriesInfoTotal, x, y, green);
            y += lineH;
        }

        if (!string.IsNullOrEmpty(SeriesInfoDescription))
        {
            y += 4;
            int maxChars = 60;
            var desc = SeriesInfoDescription;
            for (int i = 0; i < desc.Length; i += maxChars)
            {
                int len = Math.Min(maxChars, desc.Length - i);
                string line = desc.Substring(i, len);
                RenderText(line, x, y, gray);
                y += lineH - 6;
            }
            y += 6;
        }

        if (!string.IsNullOrEmpty(SeriesInfoVersions))
        {
            RenderText("Wersje:", x, y, gray);
            RenderText(SeriesInfoVersions, x + 90, y, white);
            y += lineH;
        }

        y += 8;
        RenderText("Sezony:", x, y, blue);
        y += lineH + 2;

        if (!string.IsNullOrEmpty(SeriesInfoSeasons))
        {
            var seasonLines = SeriesInfoSeasons.Split('\n');
            foreach (var line in seasonLines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                RenderText(line, x + 20, y, white);
                y += lineH - 4;
            }
        }

        var hint = new SDL.SDL_Color { r = 100, g = 100, b = 100, a = 255 };
        RenderText("Wybierz sezon z playlisty lub ESC aby zamknąć", panX + 30, panY + panH - 28, hint);
    }
}
