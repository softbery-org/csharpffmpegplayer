using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    private string? _errorText;
    private long    _errorShowTick;
    private const long ErrorDurationMs = 4000;

    public void ShowError(string message)
    {
        _errorText     = SanitizeUrls(message);
        _errorShowTick = Environment.TickCount64;
    }

    private void DrawError()
    {
        if (_errorText == null) return;
        if (Environment.TickCount64 - _errorShowTick > ErrorDurationMs) { _errorText = null; return; }
        if (_font == IntPtr.Zero) return;

        string[] lines = _errorText.Split('\n');
        int lineH = 24;
        int panW = 380, panH = 32 + lines.Length * lineH + 16;
        int panX = (_windowW - panW) / 2;
        int panY = _windowH / 2 - panH / 2;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
        SDL.SDL_SetRenderDrawColor(_renderer, 120, 20, 20, 220);
        var bg = new SDL.SDL_Rect { x = panX, y = panY, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref bg);
        SDL.SDL_SetRenderDrawColor(_renderer, 255, 80, 80, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref bg);
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);

        var red   = new SDL.SDL_Color { r = 255, g = 100, b = 100, a = 255 };
        var white = new SDL.SDL_Color { r = 230, g = 230, b = 230, a = 255 };
        int ly = panY + 14;
        RenderText("\u26A0 Błąd odtwarzania", panX + 16, ly, red); ly += lineH + 4;
        foreach (var line in lines.Skip(1))
        {
            RenderText(line, panX + 16, ly, white);
            ly += lineH;
        }
    }
}
