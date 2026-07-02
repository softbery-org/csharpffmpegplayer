using System.Text.RegularExpressions;
using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    private volatile string? _statusText;

    private static readonly Regex _urlPattern = new(@"https?://[^\s]+", RegexOptions.Compiled);

    private static string SanitizeUrls(string text)
    {
        return _urlPattern.Replace(text, "[URL]");
    }

    public void ShowStatus(string message)
    {
        _statusText = SanitizeUrls(message);
    }

    public void ClearStatus()
    {
        _statusText = null;
    }

    private void DrawStatus()
    {
        if (_statusText == null) return;
        if (_font == IntPtr.Zero) return;

        string[] lines = _statusText.Split('\n');
        int lineH = 22;
        int panW = 420, panH = 16 + lines.Length * lineH + 12;
        int panX = (_windowW - panW) / 2;
        int panY = 24;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
        SDL.SDL_SetRenderDrawColor(_renderer, 20, 40, 80, 220);
        var bg = new SDL.SDL_Rect { x = panX, y = panY, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref bg);
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 255, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref bg);
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);

        var blue  = new SDL.SDL_Color { r = 120, g = 200, b = 255, a = 255 };
        var white = new SDL.SDL_Color { r = 230, g = 230, b = 230, a = 255 };
        int ly = panY + 8;
        foreach (var line in lines)
        {
            var color = line.StartsWith("\u21BB") ? blue : white;
            RenderText(line, panX + 14, ly, color);
            ly += lineH;
        }
    }
}
