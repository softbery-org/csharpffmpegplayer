using SDL2;
using Subtitles;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    private void DrawSubtitle()
    {
        if (_subtitleFont == IntPtr.Zero || string.IsNullOrEmpty(_subtitleText)) return;

        var white = new SDL.SDL_Color { r = 255, g = 255, b = 255, a = 230 };
        var black = new SDL.SDL_Color { r = 0, g = 0, b = 0, a = 180 };

        string[] lines = _subtitleText.Split('\n');
        int lineH = 28;
        int totalH = lines.Length * lineH;
        int startY = _windowH - totalH - ProgressBarHeight - 10;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            SDLTtf.TTF_SizeUTF8(_subtitleFont, line, out int tw, out int th);
            int x = (_windowW - tw) / 2;
            int y = startY + i * lineH;

            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    RenderTextWithFont(_subtitleFont, line, x + dx, y + dy, black);
                }
            }
            RenderTextWithFont(_subtitleFont, line, x, y, white);
        }
    }

    private void RenderTextWithFont(IntPtr font, string text, int x, int y, SDL.SDL_Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        IntPtr surface = SDLTtf.TTF_RenderUTF8_Blended(font, text, color);
        if (surface == IntPtr.Zero) return;
        IntPtr tex = SDL.SDL_CreateTextureFromSurface(_renderer, surface);
        SDL.SDL_FreeSurface(surface);
        if (tex == IntPtr.Zero) return;
        SDL.SDL_QueryTexture(tex, out _, out _, out int w, out int h);
        var dst = new SDL.SDL_Rect { x = x, y = y, w = w, h = h };
        SDL.SDL_RenderCopy(_renderer, tex, IntPtr.Zero, ref dst);
        SDL.SDL_DestroyTexture(tex);
    }

    private void RenderTextInPanel(string text, int x, int y, SDL.SDL_Color color, bool center = false)
    {
        if (_font == IntPtr.Zero) return;
        IntPtr surf = SDLTtf.TTF_RenderUTF8_Blended(_font, text, color);
        if (surf == IntPtr.Zero) return;
        IntPtr tex = SDL.SDL_CreateTextureFromSurface(_renderer, surf);
        SDL.SDL_FreeSurface(surf);
        if (tex == IntPtr.Zero) return;
        SDL.SDL_QueryTexture(tex, out _, out _, out int w, out int h);
        int drawX = center ? x - w / 2 : x;
        var dst = new SDL.SDL_Rect { x = drawX, y = y, w = w, h = h };
        SDL.SDL_RenderCopy(_renderer, tex, IntPtr.Zero, ref dst);
        SDL.SDL_DestroyTexture(tex);
    }

    private void RenderText(string text, int x, int y, SDL.SDL_Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        IntPtr surface = SDLTtf.TTF_RenderUTF8_Blended(_font, text, color);
        if (surface == IntPtr.Zero) return;
        IntPtr tex = SDL.SDL_CreateTextureFromSurface(_renderer, surface);
        SDL.SDL_FreeSurface(surface);
        if (tex == IntPtr.Zero) return;
        SDL.SDL_QueryTexture(tex, out _, out _, out int w, out int h);
        var dst = new SDL.SDL_Rect { x = x, y = y, w = w, h = h };
        SDL.SDL_RenderCopy(_renderer, tex, IntPtr.Zero, ref dst);
        SDL.SDL_DestroyTexture(tex);
    }

    private void DrawBarButtonBg(int x, int y, bool highlighted)
    {
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
        byte alpha = highlighted ? (byte)80 : (byte)30;
        SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, alpha);
        var r = new SDL.SDL_Rect { x = x, y = y, w = BarBtnW, h = BarBtnH };
        SDL.SDL_RenderFillRect(_renderer, ref r);
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
    }

    private string TruncateText(string text, int maxPixels)
    {
        if (string.IsNullOrEmpty(text)) return "";
        SDLTtf.TTF_SizeUTF8(_font, text, out int w, out _);
        if (w <= maxPixels) return text;
        string ellipsis = "...";
        while (text.Length > 0)
        {
            text = text[..^1];
            SDLTtf.TTF_SizeUTF8(_font, text + ellipsis, out w, out _);
            if (w <= maxPixels) return text + ellipsis;
        }
        return ellipsis;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int h = (int)(seconds / 3600);
        int m = (int)((seconds % 3600) / 60);
        int s = (int)(seconds % 60);
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }

    private static bool Contains(SDL.SDL_Rect r, int x, int y) =>
        x >= r.x && x < r.x + r.w && y >= r.y && y < r.y + r.h;
}
