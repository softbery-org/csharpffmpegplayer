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
            int r = (i == 1) ? 28 : 22;
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
        ControlButton[] buttons = { ControlButton.Prev, ControlButton.PlayPause, ControlButton.Next, ControlButton.Stop, ControlButton.OpenFile };
        int n = buttons.Length;
        int centerX = _windowW / 2;
        int centerY = _windowH / 2;
        int spacing = 64;
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
            int r = isPlayPause ? 26 : 20;
            byte iconAlpha = hovered ? (byte)255 : (byte)200;

            // Circle background
            byte bgAlpha = hovered ? (byte)120 : (byte)50;
            byte bgR = hovered ? (byte)80 : (byte)20;
            byte bgG = hovered ? (byte)160 : (byte)20;
            byte bgB = hovered ? (byte)255 : (byte)30;
            if (isPlayPause)
            {
                bgAlpha = hovered ? (byte)200 : (byte)100;
                bgR = 80; bgG = 160; bgB = 255;
            }
            SDL.SDL_SetRenderDrawColor(_renderer, bgR, bgG, bgB, bgAlpha);
            for (int dy = -r; dy <= r; dy++)
            {
                int dx = (int)Math.Sqrt(r * r - dy * dy);
                var row = new SDL.SDL_Rect { x = bx - dx, y = centerY + dy, w = dx * 2, h = 1 };
                SDL.SDL_RenderFillRect(_renderer, ref row);
            }

            // Circle outline
            SDL.SDL_SetRenderDrawColor(_renderer, 200, 200, 200, hovered ? (byte)200 : (byte)80);
            for (int dy = -r; dy <= r; dy++)
            {
                int dx = (int)Math.Sqrt(r * r - dy * dy);
                int innerR = r - 1;
                int innerDx = innerR > 0 && Math.Abs(dy) <= innerR
                    ? (int)Math.Sqrt(innerR * innerR - dy * dy)
                    : 0;
                if (dx > innerDx)
                {
                    SDL.SDL_RenderDrawPoint(_renderer, bx - dx, centerY + dy);
                    SDL.SDL_RenderDrawPoint(_renderer, bx + dx, centerY + dy);
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
                case ControlButton.Stop:
                    DrawStopIcon(bx, centerY, iconAlpha);
                    break;
                case ControlButton.OpenFile:
                    DrawOpenFileIcon(bx, centerY, iconAlpha);
                    break;
            }
        }

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
    }

    private void DrawPlayIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, alpha);
        int s = 9;
        int offset = 3;
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
        int barW = 4, barH = 16;
        var left = new SDL.SDL_Rect { x = cx - barW - 1, y = cy - barH / 2, w = barW, h = barH };
        var right = new SDL.SDL_Rect { x = cx + 1, y = cy - barH / 2, w = barW, h = barH };
        SDL.SDL_RenderFillRect(_renderer, ref left);
        SDL.SDL_RenderFillRect(_renderer, ref right);
    }

    private void DrawStopIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 230, 230, 230, alpha);
        int s = 8;
        var r = new SDL.SDL_Rect { x = cx - s, y = cy - s, w = s * 2, h = s * 2 };
        SDL.SDL_RenderFillRect(_renderer, ref r);
    }

    private void DrawPrevIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 230, 230, 230, alpha);
        var bar = new SDL.SDL_Rect { x = cx - 7, y = cy - 7, w = 3, h = 14 };
        SDL.SDL_RenderFillRect(_renderer, ref bar);
        int s = 8;
        for (int dy = -s; dy <= s; dy++)
        {
            int halfW = (int)(s * (1.0 - (double)Math.Abs(dy) / s));
            var r = new SDL.SDL_Rect { x = cx + 4 - halfW, y = cy + dy, w = halfW, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }
    }

    private void DrawNextIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 230, 230, 230, alpha);
        int s = 8;
        for (int dy = -s; dy <= s; dy++)
        {
            int halfW = (int)(s * (1.0 - (double)Math.Abs(dy) / s));
            var r = new SDL.SDL_Rect { x = cx - 4, y = cy + dy, w = halfW, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }
        var bar = new SDL.SDL_Rect { x = cx + 4, y = cy - 7, w = 3, h = 14 };
        SDL.SDL_RenderFillRect(_renderer, ref bar);
    }

    private void DrawOpenFileIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 230, 230, 230, alpha);
        int w = 18, h = 14;
        int fx = cx - w / 2, fy = cy - h / 2;
        var tab = new SDL.SDL_Rect { x = fx, y = fy, w = 7, h = 3 };
        SDL.SDL_RenderFillRect(_renderer, ref tab);
        var body = new SDL.SDL_Rect { x = fx, y = fy + 3, w = w, h = h - 3 };
        SDL.SDL_RenderFillRect(_renderer, ref body);
        SDL.SDL_SetRenderDrawColor(_renderer, 30, 30, 30, alpha);
        int ay = cy + 1;
        for (int dy = -3; dy <= 3; dy++)
        {
            int dx = 3 - Math.Abs(dy);
            SDL.SDL_RenderDrawPoint(_renderer, cx + dx, ay + dy);
            if (dx > 0) SDL.SDL_RenderDrawPoint(_renderer, cx + dx - 1, ay + dy);
        }
    }
}
