using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    public enum TitleBarButton { None, Minimize, Maximize, Close }

    private const int TitleBarHeight = 32;
    private const int TitleBarBtnSize = 22;

    public bool IsInTitleBarDragArea(int x, int y)
    {
        if (!ControlsVisible) return false;
        if (y >= TitleBarHeight) return false;
        if (HitTestTitleBar(x, y) != TitleBarButton.None) return false;
        if (x < TitleBarHeight + 4) return false;
        return true;
    }

    public TitleBarButton HitTestTitleBar(int x, int y)
    {
        if (!ControlsVisible) return TitleBarButton.None;
        if (y >= TitleBarHeight) return TitleBarButton.None;

        int padding = 6;
        int btnS = TitleBarBtnSize;
        int closeX = _windowW - btnS - padding;
        int maxX = closeX - btnS - padding;
        int minX = maxX - btnS - padding;

        var closeRect = new SDL.SDL_Rect { x = closeX, y = padding, w = btnS, h = btnS };
        if (Contains(closeRect, x, y)) return TitleBarButton.Close;

        var maxRect = new SDL.SDL_Rect { x = maxX, y = padding, w = btnS, h = btnS };
        if (Contains(maxRect, x, y)) return TitleBarButton.Maximize;

        var minRect = new SDL.SDL_Rect { x = minX, y = padding, w = btnS, h = btnS };
        if (Contains(minRect, x, y)) return TitleBarButton.Minimize;

        return TitleBarButton.None;
    }

    public void SetTitleBarHover(int x, int y)
    {
        var hit = HitTestTitleBar(x, y);
        _minimizeHovered = hit == TitleBarButton.Minimize;
        _maximizeHovered = hit == TitleBarButton.Maximize;
        _closeHovered = hit == TitleBarButton.Close;
    }

    public void MinimizeWindow()
    {
        if (_window != IntPtr.Zero)
            SDL.SDL_MinimizeWindow(_window);
    }

    public bool IsMaximized
    {
        get
        {
            if (_window == IntPtr.Zero) return false;
            var flags = (SDL.SDL_WindowFlags)SDL.SDL_GetWindowFlags(_window);
            return flags.HasFlag(SDL.SDL_WindowFlags.SDL_WINDOW_MAXIMIZED);
        }
    }

    public void MaximizeWindow()
    {
        if (_window == IntPtr.Zero) return;
        if (IsMaximized)
            SDL.SDL_RestoreWindow(_window);
        else
            SDL.SDL_MaximizeWindow(_window);
    }

    public void RestoreWindowForDrag(int mouseX, int mouseY)
    {
        if (_window == IntPtr.Zero) return;

        var flags = (SDL.SDL_WindowFlags)SDL.SDL_GetWindowFlags(_window);
        bool isFullscreen = flags.HasFlag(SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) || flags.HasFlag(SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN_DESKTOP);
        bool isMaximized = flags.HasFlag(SDL.SDL_WindowFlags.SDL_WINDOW_MAXIMIZED);
        if (!isFullscreen && !isMaximized) return;

        SDL.SDL_GetMouseState(out int mx, out int my);

        if (isFullscreen)
            SDL.SDL_SetWindowFullscreen(_window, 0);
        if (isMaximized || isFullscreen)
            SDL.SDL_RestoreWindow(_window);

        int w = _windowedW > 0 ? _windowedW : 960;
        int h = _windowedH > 0 ? _windowedH : 540;
        SDL.SDL_SetWindowSize(_window, w, h);
        SDL.SDL_SetWindowPosition(_window, mx - mouseX, my - mouseY);
    }

    public void GetWindowPosition(out int x, out int y) =>
        SDL.SDL_GetWindowPosition(_window, out x, out y);

    public void SetWindowPosition(int x, int y) =>
        SDL.SDL_SetWindowPosition(_window, x, y);

    private void DrawTitleBarButtons()
    {
        if (!ControlsVisible) return;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        // Title bar background strip so buttons are visible over video
        SDL.SDL_SetRenderDrawColor(_renderer, 20, 20, 24, 120);
        var titleBarBg = new SDL.SDL_Rect { x = 0, y = 0, w = _windowW, h = TitleBarHeight };
        SDL.SDL_RenderFillRect(_renderer, ref titleBarBg);

        // Left-side app logo
        DrawTitleBarLogo();

        int padding = 6;
        int btnS = TitleBarBtnSize;
        int closeX = _windowW - btnS - padding;
        int maxX = closeX - btnS - padding;
        int minX = maxX - btnS - padding;
        int y = padding;

        var subtle = new SDL.SDL_Color { r = 220, g = 220, b = 220, a = 180 };
        var hover = new SDL.SDL_Color { r = 0, g = 0, b = 0, a = 255 };

        // Minimize button
        DrawTitleBarButtonBg(minX, y, _minimizeHovered);
        DrawTitleBarButtonLabel("—", minX, y, _minimizeHovered ? hover : subtle);

        // Maximize button
        DrawTitleBarButtonBg(maxX, y, _maximizeHovered);
        DrawTitleBarButtonLabel("□", maxX, y, _maximizeHovered ? hover : subtle);

        // Close button
        DrawTitleBarButtonBg(closeX, y, _closeHovered);
        DrawTitleBarButtonLabel("X", closeX, y, _closeHovered ? hover : subtle);

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
    }

    private void DrawTitleBarButtonBg(int x, int y, bool hovered)
    {
        if (!hovered) return;
        SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, 255);
        var r = new SDL.SDL_Rect { x = x, y = y, w = TitleBarBtnSize, h = TitleBarBtnSize };
        SDL.SDL_RenderFillRect(_renderer, ref r);
    }

    private void DrawTitleBarButtonLabel(string text, int x, int y, SDL.SDL_Color color)
    {
        if (_font == IntPtr.Zero) return;
        IntPtr surf = SDLTtf.TTF_RenderUTF8_Blended(_font, text, color);
        if (surf == IntPtr.Zero) return;
        IntPtr tex = SDL.SDL_CreateTextureFromSurface(_renderer, surf);
        SDL.SDL_FreeSurface(surf);
        if (tex == IntPtr.Zero) return;
        SDL.SDL_QueryTexture(tex, out _, out _, out int w, out int h);
        var dst = new SDL.SDL_Rect { x = x + (TitleBarBtnSize - w) / 2, y = y + (TitleBarBtnSize - h) / 2, w = w, h = h };
        SDL.SDL_RenderCopy(_renderer, tex, IntPtr.Zero, ref dst);
        SDL.SDL_DestroyTexture(tex);
    }

    private void DrawTitleBarLogo()
    {
        int logoSize = 20;
        int logoX = 8;
        int logoY = (TitleBarHeight - logoSize) / 2;
        int cx = logoX + logoSize / 2;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        // Audio wave bars
        int barCount = 5;
        int barW = 3;
        int gap = 2;
        int totalW = barCount * barW + (barCount - 1) * gap;
        int startX = cx - totalW / 2;
        int baseY = logoY + logoSize - 3;
        int[] heights = { 6, 10, 14, 9, 5 };

        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 255, 220);
        for (int i = 0; i < barCount; i++)
        {
            int h = heights[i];
            int bx = startX + i * (barW + gap);
            int by = baseY - h;
            var r = new SDL.SDL_Rect { x = bx, y = by, w = barW, h = h };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
    }
}
