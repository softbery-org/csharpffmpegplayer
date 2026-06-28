using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    public bool AboutVisible = false;

    private void DrawAbout()
    {
        if (_font == IntPtr.Zero) return;

        int panW = 420, panH = 220;
        int panX = (_windowW - panW) / 2;
        int panY = (_windowH - panH) / 2;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 120);
        var shadow = new SDL.SDL_Rect { x = panX + 4, y = panY + 4, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref shadow);

        SDL.SDL_SetRenderDrawColor(_renderer, 28, 28, 32, 235);
        var bg = new SDL.SDL_Rect { x = panX, y = panY, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref bg);

        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 255, 200);
        SDL.SDL_RenderDrawRect(_renderer, ref bg);

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);

        var white  = new SDL.SDL_Color { r = 230, g = 230, b = 230, a = 255 };
        var blue   = new SDL.SDL_Color { r = 80,  g = 160, b = 255, a = 255 };
        var gray   = new SDL.SDL_Color { r = 150, g = 150, b = 150, a = 255 };

        int lx = panX + 24;
        int ly = panY + 20;
        int lineH = 26;

        RenderText("CSharp FFmpeg Player", lx, ly, blue);           ly += lineH + 4;
        RenderText("Wersja: 1.0.0", lx, ly, white);                 ly += lineH;
        RenderText("Silnik: FFmpeg + SDL2 (.NET 8)", lx, ly, white); ly += lineH;
        RenderText("Autor: Softbery by Paweł Tobis", lx, ly, white);                ly += lineH;
        RenderText("Licencja: MIT", lx, ly, gray);                   ly += lineH + 8;
        RenderText("Skróty: SPACJA pauza  F pełny ekran", lx, ly, gray);   ly += lineH - 4;
        RenderText("TAB playlista  N/P następny/poprzedni  Q wyjście", lx, ly, gray);

        var hint = new SDL.SDL_Color { r = 100, g = 100, b = 100, a = 255 };
        RenderText("[?] lub ESC aby zamknąć", panX + panW - 190, panY + panH - 22, hint);
    }
}
