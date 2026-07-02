using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    public enum ControlButton { None, Prev, PlayPause, Next, Stop, OpenFile }

    public ControlButton HitTestControl(int x, int y)
    {
        if (!ControlsVisible) return ControlButton.None;
        for (int i = 0; i < _overlayButtonXs.Length; i++)
        {
            int dx = x - _overlayButtonXs[i];
            int dy = y - _overlayButtonY;
            int r = (i == 1) ? 52 : 32;
            if (dx * dx + dy * dy <= r * r)
                return (ControlButton)(i + 1);
        }
        return ControlButton.None;
    }

    public void SetHoveredButton(int idx)
    {
        _hoveredButton = idx;
    }

    private void DrawControls()
    {
        ControlButton[] buttons = { ControlButton.Prev, ControlButton.PlayPause, ControlButton.Next };
        int n = buttons.Length;
        int centerX = _windowW / 2;
        int centerY = _windowH / 2;
        int spacing = 110;
        int totalW = (n - 1) * spacing;
        int startX = centerX - totalW / 2;

        _overlayButtonY = centerY;
        _overlayButtonXs = new int[n];
        for (int i = 0; i < n; i++)
            _overlayButtonXs[i] = startX + i * spacing;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        for (int i = 0; i < n; i++)
        {
            int bx = _overlayButtonXs[i];
            bool hovered = _hoveredButton == i;
            bool isPlayPause = buttons[i] == ControlButton.PlayPause;
            int r = isPlayPause ? 50 : 30;
            byte iconAlpha = hovered ? (byte)255 : (byte)200;

            // Circle background (gray tones)
            byte bgAlpha = hovered ? (byte)140 : (byte)60;
            byte bgR = hovered ? (byte)100 : (byte)50;
            byte bgG = hovered ? (byte)110 : (byte)55;
            byte bgB = hovered ? (byte)120 : (byte)60;
            if (isPlayPause)
            {
                bgAlpha = hovered ? (byte)180 : (byte)90;
                bgR = hovered ? (byte)110 : (byte)60;
                bgG = hovered ? (byte)120 : (byte)65;
                bgB = hovered ? (byte)130 : (byte)70;
            }
            SDL.SDL_SetRenderDrawColor(_renderer, bgR, bgG, bgB, bgAlpha);
            for (int dy = -r; dy <= r; dy++)
            {
                int dx = (int)Math.Sqrt(r * r - dy * dy);
                var row = new SDL.SDL_Rect { x = bx - dx, y = centerY + dy, w = dx * 2, h = 1 };
                SDL.SDL_RenderFillRect(_renderer, ref row);
            }

            // Circle outline (only for center PlayPause button)
            if (isPlayPause)
            {
                byte outlineAlpha = hovered ? (byte)230 : (byte)120;
                SDL.SDL_SetRenderDrawColor(_renderer, 210, 210, 215, outlineAlpha);
                for (int dy = -r; dy <= r; dy++)
                {
                    int dx = (int)Math.Sqrt(r * r - dy * dy);
                    int innerR = r - 4;
                    int innerDx = innerR > 0 && Math.Abs(dy) <= innerR
                        ? (int)Math.Sqrt(innerR * innerR - dy * dy)
                        : 0;
                    if (dx > innerDx)
                    {
                        var rowL = new SDL.SDL_Rect { x = bx - dx, y = centerY + dy, w = dx - innerDx, h = 1 };
                        SDL.SDL_RenderFillRect(_renderer, ref rowL);
                        var rowR = new SDL.SDL_Rect { x = bx + innerDx, y = centerY + dy, w = dx - innerDx, h = 1 };
                        SDL.SDL_RenderFillRect(_renderer, ref rowR);
                    }
                }
            }

            switch (buttons[i])
            {
                case ControlButton.Prev:
                    DrawPrevIcon(bx, centerY, iconAlpha);
                    break;
                case ControlButton.PlayPause:
                    if (IsPlaying)
                        DrawPauseIcon(bx, centerY, iconAlpha);
                    else
                        DrawPlayIcon(bx, centerY, iconAlpha);
                    break;
                case ControlButton.Next:
                    DrawNextIcon(bx, centerY, iconAlpha);
                    break;
            }
        }

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
    }

    private void DrawPlayIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, alpha);
        int s = 22;
        int offset = 7;
        for (int dy = -s; dy <= s; dy++)
        {
            int halfW = (int)(s * (1.0 - (double)Math.Abs(dy) / s));
            var r = new SDL.SDL_Rect { x = cx - offset, y = cy + dy, w = halfW, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }
    }

    private void DrawPauseIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, alpha);
        int barW = 10, barH = 37;
        var left = new SDL.SDL_Rect { x = cx - barW - 3, y = cy - barH / 2, w = barW, h = barH };
        var right = new SDL.SDL_Rect { x = cx + 3, y = cy - barH / 2, w = barW, h = barH };
        SDL.SDL_RenderFillRect(_renderer, ref left);
        SDL.SDL_RenderFillRect(_renderer, ref right);
    }

    private void DrawPrevIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 230, 230, 230, alpha);
        var bar = new SDL.SDL_Rect { x = cx - 10, y = cy - 10, w = 4, h = 20 };
        SDL.SDL_RenderFillRect(_renderer, ref bar);
        int s = 11;
        for (int dy = -s; dy <= s; dy++)
        {
            int halfW = (int)(s * (1.0 - (double)Math.Abs(dy) / s));
            var r = new SDL.SDL_Rect { x = cx + 6 - halfW, y = cy + dy, w = halfW, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }
    }

    private void DrawNextIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 230, 230, 230, alpha);
        int s = 11;
        for (int dy = -s; dy <= s; dy++)
        {
            int halfW = (int)(s * (1.0 - (double)Math.Abs(dy) / s));
            var r = new SDL.SDL_Rect { x = cx - 6, y = cy + dy, w = halfW, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }
        var bar = new SDL.SDL_Rect { x = cx + 6, y = cy - 10, w = 4, h = 20 };
        SDL.SDL_RenderFillRect(_renderer, ref bar);
    }

}
