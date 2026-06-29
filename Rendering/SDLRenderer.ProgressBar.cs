using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    public const int ThumbW = 160;
    public const int ThumbH = 90;
    private IntPtr _thumbTexture = IntPtr.Zero;
    private double _thumbTime = -1;
    public double ProgressHoverTime = -1;

    private byte[]? _pendingThumbRgba;
    private double  _pendingThumbTime = -1;
    private readonly object _thumbLock = new();

    [System.Runtime.InteropServices.DllImport("SDL2", EntryPoint = "SDL_UpdateTexture", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern unsafe int SDL_UpdateTexture_NullRect(IntPtr texture, IntPtr rect, void* pixels, int pitch);

    public void SetThumbnail(byte[] rgba, double time)
    {
        lock (_thumbLock) { _pendingThumbRgba = rgba; _pendingThumbTime = time; }
    }

    private void ApplyPendingThumbnail()
    {
        byte[]? rgba; double time;
        lock (_thumbLock)
        {
            if (_pendingThumbRgba == null) return;
            rgba = _pendingThumbRgba; time = _pendingThumbTime;
            _pendingThumbRgba = null;
        }
        if (_thumbTexture != IntPtr.Zero) { SDL.SDL_DestroyTexture(_thumbTexture); _thumbTexture = IntPtr.Zero; }
        _thumbTexture = SDL.SDL_CreateTexture(_renderer,
            SDL.SDL_PIXELFORMAT_RGB24, (int)SDL.SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC,
            ThumbW, ThumbH);
        if (_thumbTexture == IntPtr.Zero) return;
        unsafe { fixed (byte* p = rgba) SDL_UpdateTexture_NullRect(_thumbTexture, IntPtr.Zero, p, ThumbW * 3); }
        _thumbTime = time;
    }

    public void ClearThumbnail()
    {
        lock (_thumbLock) { _pendingThumbRgba = null; }
        if (_thumbTexture != IntPtr.Zero) { SDL.SDL_DestroyTexture(_thumbTexture); _thumbTexture = IntPtr.Zero; }
        _thumbTime = -1;
    }

    public int GetProgressBarY() { SDL.SDL_GetWindowSize(_window, out _windowW, out _windowH); return _windowH - ProgressBarHeight - ProgressBarBottomMargin; }

    private void DrawProgressBar()
    {
        int barY = _windowH - ProgressBarHeight - ProgressBarBottomMargin;
        int margin = 20;
        int barX = PlaylistPanelVisible ? PlaylistPanelWidth + margin : margin;
        int barW = _windowW - barX - margin;
        int trackH = TrackHeight;
        int trackY = _windowH - trackH - 4 - ProgressBarBottomMargin;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
        for (int i = 0; i < ProgressBarHeight; i++)
        {
            byte alpha = (byte)(180 * (i / (double)ProgressBarHeight));
            SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, alpha);
            var row = new SDL.SDL_Rect { x = 0, y = barY + i, w = _windowW, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref row);
        }

        DrawProgressBarText(barY, barX, barW);

        SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 60, 180);
        var trackRect = new SDL.SDL_Rect { x = barX, y = trackY, w = barW, h = trackH };
        SDL.SDL_RenderFillRect(_renderer, ref trackRect);

        double frac = _duration > 0 ? Math.Clamp(_progress / _duration, 0, 1) : 0;
        int fillW = (int)(barW * frac);
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 255, 230);
        if (fillW > 0)
        {
            var fillRect = new SDL.SDL_Rect { x = barX, y = trackY, w = fillW, h = trackH };
            SDL.SDL_RenderFillRect(_renderer, ref fillRect);
        }

        int knobR = 6;
        int knobX = barX + fillW;
        int knobY = trackY + trackH / 2;
        SDL.SDL_SetRenderDrawColor(_renderer, 220, 230, 255, 230);
        for (int dy = -knobR; dy <= knobR; dy++)
        {
            int dx = (int)Math.Sqrt(knobR * knobR - dy * dy);
            var lineRect = new SDL.SDL_Rect { x = knobX - dx, y = knobY + dy, w = dx * 2, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref lineRect);
        }

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
    }

    private void DrawProgressBarText(int barY, int barX, int barW)
    {
        if (_font == IntPtr.Zero) return;

        var white  = new SDL.SDL_Color { r = 220, g = 220, b = 220, a = 255 };
        var active = new SDL.SDL_Color { r = 80,  g = 160, b = 255, a = 255 };
        int textY  = barY + 6;
        int btnY   = barY + 6;

        int bx = barX;

        _btnBarPlaylist = new SDL.SDL_Rect { x = bx, y = btnY, w = BarBtnW, h = BarBtnH };
        var plCol = PlaylistPanelVisible ? active : white;
        DrawBarButtonBg(bx, btnY, PlaylistPanelVisible);
        RenderText("PL", bx + 3, btnY + 3, plCol);
        bx += BarBtnW + 4;

        _btnBarRepeat = new SDL.SDL_Rect { x = bx, y = btnY, w = BarBtnW + 10, h = BarBtnH };
        string repeatLabel = PlaylistRepeatMode switch
        {
            RepeatMode.All     => "All",
            RepeatMode.One     => "One",
            RepeatMode.Shuffle => "Shuf",
            _                  => "Once",
        };
        DrawBarButtonBg(bx, btnY, false);
        RenderText(repeatLabel, bx + 4, btnY + 3, white);
        bx += BarBtnW + 14;

        string titleText = TruncateText(_title, (barX + barW / 2) - bx - 4);
        RenderText(titleText, bx, textY, white);

        int rx = barX + barW;
        rx -= BarBtnW + 2;
        _btnBarAbout = new SDL.SDL_Rect { x = rx, y = btnY, w = BarBtnW, h = BarBtnH };
        DrawBarButtonBg(rx, btnY, AboutVisible);
        var aboutCol = AboutVisible ? active : white;
        RenderText("?", rx + 8, btnY + 3, aboutCol);

        string timeText = $"{FormatTime(_progress)} / {FormatTime(_duration)}";
        SDLTtf.TTF_SizeUTF8(_font, timeText, out int tw, out int _);
        RenderText(timeText, rx - tw - 8, textY, white);
    }

    private void DrawThumbnailPreview()
    {
        int margin = 20;
        int barW   = _windowW - margin * 2;
        int barX   = margin;
        int trackY = _windowH - TrackHeight - 4 - ProgressBarBottomMargin;

        if (_duration <= 0) return;
        double frac = Math.Clamp(ProgressHoverTime / _duration, 0, 1);
        int cx = barX + (int)(barW * frac);

        int pad  = 4;
        int tw   = ThumbW + pad * 2;
        int th   = ThumbH + pad * 2 + (_font != IntPtr.Zero ? 18 : 0);
        int tx   = Math.Clamp(cx - tw / 2, 0, _windowW - tw);
        int ty   = trackY - th - 80;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 100);
        var shadow = new SDL.SDL_Rect { x = tx + 3, y = ty + 3, w = tw, h = th };
        SDL.SDL_RenderFillRect(_renderer, ref shadow);

        SDL.SDL_SetRenderDrawColor(_renderer, 20, 20, 24, 230);
        var bg = new SDL.SDL_Rect { x = tx, y = ty, w = tw, h = th };
        SDL.SDL_RenderFillRect(_renderer, ref bg);

        SDL.SDL_SetRenderDrawColor(_renderer, 180, 180, 180, 160);
        SDL.SDL_RenderDrawRect(_renderer, ref bg);
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);

        if (_thumbTexture != IntPtr.Zero && Math.Abs(_thumbTime - ProgressHoverTime) < 3.0)
        {
            var imgDst = new SDL.SDL_Rect { x = tx + pad, y = ty + pad, w = ThumbW, h = ThumbH };
            SDL.SDL_RenderCopy(_renderer, _thumbTexture, IntPtr.Zero, ref imgDst);
        }
        else
        {
            SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
            SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 70, 200);
            var ph = new SDL.SDL_Rect { x = tx + pad, y = ty + pad, w = ThumbW, h = ThumbH };
            SDL.SDL_RenderFillRect(_renderer, ref ph);
            SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
        }

        if (_font != IntPtr.Zero)
        {
            string label = FormatTime(ProgressHoverTime);
            var white = new SDL.SDL_Color { r = 220, g = 220, b = 220, a = 255 };
            SDLTtf.TTF_SizeUTF8(_font, label, out int lw, out _);
            RenderText(label, tx + (tw - lw) / 2, ty + pad + ThumbH + 2, white);
        }

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
        SDL.SDL_SetRenderDrawColor(_renderer, 200, 200, 200, 120);
        SDL.SDL_RenderDrawLine(_renderer, cx, ty + th, cx, trackY);
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
    }
}
