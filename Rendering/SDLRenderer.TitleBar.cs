using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    public enum TitleBarButton { None, Minimize, Close }

    private const int TitleBarHeight = 32;

    public bool IsInTitleBarDragArea(int x, int y)
    {
        if (y >= TitleBarHeight) return false;
        if (HitTestTitleBar(x, y) != TitleBarButton.None) return false;
        if (x < TitleBarHeight + 4) return false;
        return true;
    }

    public TitleBarButton HitTestTitleBar(int x, int y)
    {
        if (y >= TitleBarHeight) return TitleBarButton.None;

        int btnW = 46;
        int btnH = TitleBarHeight;

        int closeX = _windowW - btnW;
        var closeRect = new SDL.SDL_Rect { x = closeX, y = 0, w = btnW, h = btnH };
        if (Contains(closeRect, x, y)) return TitleBarButton.Close;

        int minX = closeX - btnW;
        var minRect = new SDL.SDL_Rect { x = minX, y = 0, w = btnW, h = btnH };
        if (Contains(minRect, x, y)) return TitleBarButton.Minimize;

        return TitleBarButton.None;
    }

    public void SetTitleBarHover(int x, int y)
    {
        var hit = HitTestTitleBar(x, y);
        _minimizeHovered = hit == TitleBarButton.Minimize;
        _closeHovered = hit == TitleBarButton.Close;
    }

    public void MinimizeWindow()
    {
        if (_window != IntPtr.Zero)
            SDL.SDL_MinimizeWindow(_window);
    }

    public void GetWindowPosition(out int x, out int y) =>
        SDL.SDL_GetWindowPosition(_window, out x, out y);

    public void SetWindowPosition(int x, int y) =>
        SDL.SDL_SetWindowPosition(_window, x, y);

    private void DrawTitleBarButtons()
    {
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        int btnW = 46;
        int btnH = TitleBarHeight;

        int closeX = _windowW - btnW;
        int minX = closeX - btnW;

        // Minimize button
        byte minBgAlpha = _minimizeHovered ? (byte)100 : (byte)0;
        if (minBgAlpha > 0)
        {
            SDL.SDL_SetRenderDrawColor(_renderer, 80, 80, 80, minBgAlpha);
            var minBg = new SDL.SDL_Rect { x = minX, y = 0, w = btnW, h = btnH };
            SDL.SDL_RenderFillRect(_renderer, ref minBg);
        }
        byte minIconAlpha = _minimizeHovered ? (byte)255 : (byte)180;
        SDL.SDL_SetRenderDrawColor(_renderer, 220, 220, 220, minIconAlpha);
        int minLineY = btnH / 2;
        int minLineW = 12;
        int minLineX = minX + (btnW - minLineW) / 2;
        var minLine = new SDL.SDL_Rect { x = minLineX, y = minLineY, w = minLineW, h = 2 };
        SDL.SDL_RenderFillRect(_renderer, ref minLine);

        // Close button
        byte closeBgAlpha = _closeHovered ? (byte)220 : (byte)0;
        if (closeBgAlpha > 0)
        {
            SDL.SDL_SetRenderDrawColor(_renderer, 232, 17, 35, closeBgAlpha);
            var closeBg = new SDL.SDL_Rect { x = closeX, y = 0, w = btnW, h = btnH };
            SDL.SDL_RenderFillRect(_renderer, ref closeBg);
        }
        byte closeIconAlpha = _closeHovered ? (byte)255 : (byte)180;
        SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, closeIconAlpha);
        int cx = closeX + btnW / 2;
        int cy = btnH / 2;
        int xSize = 6;
        for (int d = -xSize; d <= xSize; d++)
        {
            SDL.SDL_RenderDrawPoint(_renderer, cx + d, cy + d);
            SDL.SDL_RenderDrawPoint(_renderer, cx + d, cy - d);
            SDL.SDL_RenderDrawPoint(_renderer, cx + d + 1, cy + d);
            SDL.SDL_RenderDrawPoint(_renderer, cx + d + 1, cy - d);
        }

        // Left-side app logo
        DrawTitleBarLogo();

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
    }

    private void DrawTitleBarLogo()
    {
        int logoSize = 20;
        int logoX = 8;
        int logoY = (TitleBarHeight - logoSize) / 2;
        int cx = logoX + logoSize / 2;
        int cy = logoY + logoSize / 2;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        // Play triangle in blue
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 255, 220);
        int s = 7;
        for (int dy = -s; dy <= s; dy++)
        {
            int halfW = (int)(s * (1.0 - (double)Math.Abs(dy) / s));
            var r = new SDL.SDL_Rect { x = cx - 3, y = cy + dy, w = halfW, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }

        // Circle outline around the triangle
        int radius = logoSize / 2 - 1;
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 255, 160);
        for (int dy = -radius; dy <= radius; dy++)
        {
            int dx = (int)Math.Sqrt(radius * radius - dy * dy);
            SDL.SDL_RenderDrawPoint(_renderer, cx - dx, cy + dy);
            SDL.SDL_RenderDrawPoint(_renderer, cx + dx, cy + dy);
            SDL.SDL_RenderDrawPoint(_renderer, cx - dx + 1, cy + dy);
            SDL.SDL_RenderDrawPoint(_renderer, cx + dx - 1, cy + dy);
        }

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
    }
}
