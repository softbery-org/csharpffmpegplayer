using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    public bool AboutVisible = false;

    private void DrawAbout()
    {
        if (_font == IntPtr.Zero) return;

        int panW = 720, panH = 440;
        int panX = (_windowW - panW) / 2;
        int panY = (_windowH - panH) / 2;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 140);
        var shadow = new SDL.SDL_Rect { x = panX + 6, y = panY + 6, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref shadow);

        SDL.SDL_SetRenderDrawColor(_renderer, 32, 34, 40, 245);
        var bg = new SDL.SDL_Rect { x = panX, y = panY, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref bg);

        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 255, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref bg);

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);

        var white = new SDL.SDL_Color { r = 230, g = 230, b = 230, a = 255 };
        var blue  = new SDL.SDL_Color { r = 80,  g = 160, b = 255, a = 255 };
        var gray  = new SDL.SDL_Color { r = 150, g = 150, b = 150, a = 255 };
        var muted = new SDL.SDL_Color { r = 110, g = 110, b = 110, a = 255 };

        int lineH = 32;
        int col1X = panX + 40;
        int col2X = panX + 380;
        int y = panY + 30;

        RenderText("CSharp FFmpeg Player", panX + 40, y, blue);
        y += lineH + 4;
        RenderText("Wersja 3.30.6.bbe6612  |  FFmpeg + SDL2 (.NET 8)  |  Softbery by Paweł Tobis", panX + 40, y, muted);
        y += lineH + 8;

        // Section: Playback
        RenderText("Odtwarzanie", col1X, y, blue);
        int headerY = y;
        y += lineH + 4;
        RenderText("Space", col1X, y, white);        RenderText("Play / Pause", col1X + 110, y, gray);     y += lineH;
        RenderText("← / →", col1X, y, white);        RenderText("Seek -10s / +10s", col1X + 110, y, gray); y += lineH;
        RenderText("N / P", col1X, y, white);        RenderText("Next / Previous track", col1X + 110, y, gray); y += lineH;
        RenderText("F", col1X, y, white);            RenderText("Toggle fullscreen", col1X + 110, y, gray); y += lineH;
        RenderText("ESC", col1X, y, white);          RenderText("Exit fullscreen / close / quit", col1X + 110, y, gray); y += lineH;

        // Section: Playlist / Window
        int plY = headerY;
        RenderText("Playlista / Okno", col2X, plY, blue);
        plY += lineH + 4;
        RenderText("Tab", col2X, plY, white);        RenderText("Toggle playlist panel", col2X + 110, plY, gray);     plY += lineH;
        RenderText("↑ / ↓", col2X, plY, white);      RenderText("Select item (panel visible)", col2X + 110, plY, gray); plY += lineH;
        RenderText("Enter", col2X, plY, white);      RenderText("Play selected item", col2X + 110, plY, gray);     plY += lineH;
        RenderText("Del / Backspace", col2X, plY, white); RenderText("Remove selected item", col2X + 110, plY, gray); plY += lineH;
        RenderText("T", col2X, plY, white);          RenderText("Always on top", col2X + 110, plY, gray);            plY += lineH;
        RenderText("Q", col2X, plY, white);          RenderText("Save session and quit", col2X + 110, plY, gray);

        var hint = new SDL.SDL_Color { r = 100, g = 100, b = 100, a = 255 };
        RenderText("Press [?] or ESC to close", panX + panW - 220, panY + panH - 28, hint);
    }
}
