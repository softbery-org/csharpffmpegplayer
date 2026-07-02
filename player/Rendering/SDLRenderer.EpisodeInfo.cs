using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    public bool EpisodeInfoVisible = false;
    public string EpisodeInfoTitle = "";
    public string EpisodeInfoDescription = "";
    public string EpisodeInfoSources = "";
    public string EpisodeInfoSeasonEp = "";

    private void DrawEpisodeInfo()
    {
        if (_font == IntPtr.Zero) return;

        bool hasVideo = _texture != IntPtr.Zero;
        int panW, panH, panX, panY;

        if (hasVideo)
        {
            // Compact overlay in bottom-right corner — doesn't block video
            panW = 440;
            panH = 180;
            panX = _windowW - panW - 16;
            panY = _windowH - panH - 60;
        }
        else
        {
            // Full centered panel when no video playing
            panW = 560;
            panH = 300;
            panX = (_windowW - panW) / 2;
            panY = (_windowH - panH) / 2;
        }

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        // Shadow
        SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, (byte)(hasVideo ? 160 : 140));
        var shadow = new SDL.SDL_Rect { x = panX + 4, y = panY + 4, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref shadow);

        // Background — semi-transparent when video playing
        SDL.SDL_SetRenderDrawColor(_renderer, 20, 22, 30, (byte)(hasVideo ? 210 : 250));
        var bg = new SDL.SDL_Rect { x = panX, y = panY, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref bg);

        // Border
        SDL.SDL_SetRenderDrawColor(_renderer, 100, 180, 255, 200);
        SDL.SDL_RenderDrawRect(_renderer, ref bg);

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);

        var white = new SDL.SDL_Color { r = 230, g = 230, b = 230, a = 255 };
        var blue  = new SDL.SDL_Color { r = 100, g = 180, b = 255, a = 255 };
        var gray  = new SDL.SDL_Color { r = 150, g = 150, b = 150, a = 255 };
        var green = new SDL.SDL_Color { r = 100, g = 220, b = 130, a = 255 };
        var hint  = new SDL.SDL_Color { r = 100, g = 100, b = 100, a = 255 };

        int lineH = hasVideo ? 22 : 26;
        int x = panX + 16;
        int y = panY + 12;

        if (!string.IsNullOrEmpty(EpisodeInfoSeasonEp))
        {
            RenderText(EpisodeInfoSeasonEp, x, y, green);
            y += lineH;
        }

        // Title — truncate if too long for compact mode
        string title = EpisodeInfoTitle;
        if (hasVideo && title.Length > 50)
            title = title.Substring(0, 47) + "...";
        RenderText(title, x, y, blue);
        y += lineH + 2;

        if (!string.IsNullOrEmpty(EpisodeInfoDescription))
        {
            int maxChars = hasVideo ? 52 : 62;
            int maxLines = hasVideo ? 3 : 6;
            var desc = EpisodeInfoDescription;
            int lineCount = 0;
            for (int i = 0; i < desc.Length && lineCount < maxLines; i += maxChars)
            {
                int len = Math.Min(maxChars, desc.Length - i);
                string line = desc.Substring(i, len);
                RenderText(line, x, y, gray);
                y += lineH - 4;
                lineCount++;
            }
        }

        if (!string.IsNullOrEmpty(EpisodeInfoSources))
        {
            y = panY + panH - 38;
            string srcLabel = hasVideo ? EpisodeInfoSources : "Dostępne źródła:";
            if (!hasVideo)
            {
                RenderText("Dostępne źródła:", x, y, blue);
                y += lineH;
                RenderText(EpisodeInfoSources, x, y, white);
            }
            else
            {
                // Compact: just show sources on one line
                string truncated = EpisodeInfoSources.Length > 52
                    ? EpisodeInfoSources.Substring(0, 49) + "..."
                    : EpisodeInfoSources;
                RenderText($"Źródła: {truncated}", x, y, gray);
            }
        }

        RenderText("Enter = odtwórz | ESC = zamknij", panX + 16, panY + panH - 18, hint);
    }
}
