using SDL2;
using System.Runtime.InteropServices;
using Subtitles;

namespace CSharpFFmpeg;

public sealed class SDLRenderer : IDisposable
{
    private IntPtr _window;
    private IntPtr _renderer;
    private IntPtr _texture;
    private int _width;
    private int _height;
    private bool _audioInitialized;
    private SDL.SDL_AudioCallback? _audioCallbackDelegate;
    private Action<byte[]>? _userAudioCallback;
    private int _windowW;
    private int _windowH;
    private const int ProgressBarHeight = 60;
    private const int TextHeight = 18;
    private const int TrackHeight = 4;
    private const int BarBtnH = 22;
    private const int BarBtnW = 28;
    private const int OverlayIconSize = 22;
    private const int OverlayIconSpacing = 80;
    private const int OverlayBottomMargin = 28;
    private const int VolSliderW = 100;
    private const int VolSliderH = 4;
    private double _progress;
    private double _duration;
    private string _title = "";
    private IntPtr _font;
    private IntPtr _subtitleFont;
    private bool _ttfInit;
    private string? _subtitleText;
    private long _fpsLastTicks;
    private int _fpsFrameCount;
    private int _displayedFps;

    public double Duration { set => _duration = value; }
    public string Title { set => _title = value; }
    public string? SubtitleText { set => _subtitleText = value; }
    public double CurrentFps => _displayedFps;

    public void InitVideo(int width, int height)
    {
        _width = width;
        _height = height;
        SDL.SDL_Init(SDL.SDL_INIT_VIDEO);
        SDL.SDL_SetHint(SDL.SDL_HINT_RENDER_SCALE_QUALITY, "1");
        _window = SDL.SDL_CreateWindow(
            "CSharp FFmpeg Player",
            SDL.SDL_WINDOWPOS_CENTERED, SDL.SDL_WINDOWPOS_CENTERED,
            width, height, SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN | SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
        SDL.SDL_GetWindowSize(_window, out _windowW, out _windowH);
        _renderer = SDL.SDL_CreateRenderer(_window, -1,
            SDL.SDL_RendererFlags.SDL_RENDERER_ACCELERATED | SDL.SDL_RendererFlags.SDL_RENDERER_PRESENTVSYNC);
        _texture = SDL.SDL_CreateTexture(_renderer,
            SDL.SDL_PIXELFORMAT_IYUV,
            (int)SDL.SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING,
            width, height);

        InitFont();
    }

    private void InitFont()
    {
        if (SDLTtf.TTF_Init() < 0) return;
        _ttfInit = true;
        string[] fontPaths = [
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans.ttf",
        ];
        foreach (var path in fontPaths)
        {
            if (File.Exists(path))
            {
                _font = SDLTtf.TTF_OpenFont(path, 14);
                if (_font != IntPtr.Zero)
                {
                    _subtitleFont = SDLTtf.TTF_OpenFont(path, 22);
                    return;
                }
            }
        }
    }

    public void InitAudio(int sampleRate, int channels, Action<byte[]> audioCallback)
    {
        SDL.SDL_InitSubSystem(SDL.SDL_INIT_AUDIO);
        _userAudioCallback = audioCallback;
        _audioCallbackDelegate = (userdata, stream, len) =>
        {
            var buf = new byte[len];
            _userAudioCallback(buf);
            float vol = Math.Clamp(Volume, 0f, 1.5f);
            if (Math.Abs(vol - 1.0f) > 0.005f)
            {
                // Scale S16LE samples in-place, clamp to short range to avoid clipping
                for (int i = 0; i + 1 < len; i += 2)
                {
                    short s = (short)(buf[i] | (buf[i + 1] << 8));
                    int scaled = (int)(s * vol);
                    scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
                    buf[i]     = (byte)(scaled & 0xFF);
                    buf[i + 1] = (byte)((scaled >> 8) & 0xFF);
                }
            }
            Marshal.Copy(buf, 0, stream, len);
        };
        var spec = new SDL.SDL_AudioSpec
        {
            freq = sampleRate,
            format = SDL.AUDIO_S16LSB,
            channels = (byte)channels,
            silence = 0,
            samples = 1024,
            callback = _audioCallbackDelegate
        };
        var obtained = new SDL.SDL_AudioSpec();
        if (SDL.SDL_OpenAudio(ref spec, out obtained) < 0)
            throw new InvalidOperationException($"SDL_OpenAudio failed: {SDL.SDL_GetError()}");
        SDL.SDL_PauseAudio(0);
        _audioInitialized = true;
    }

    public bool ProgressBarVisible = true;
    public bool ControlsVisible = true;
    public bool IsPlaying = true;
    public bool PlaylistPanelVisible = false;

    // Volume: 0.0 (mute) .. 1.0 (full)
    public float Volume = 1.0f;

    // Playlist panel data
    private string[] _playlistNames = Array.Empty<string>();
    private double[] _playlistDurations = Array.Empty<double>();
    private int _playlistCurrentIndex = -1;
    private double _playlistCurrentTime = 0;
    public const int PlaylistPanelWidth = 340;
    private const int PlaylistItemHeight = 42;
    private int _playlistScrollOffset = 0;
    public RepeatMode PlaylistRepeatMode = RepeatMode.Once;

    // Panel header button rects (for hit-testing), set each frame
    private SDL.SDL_Rect _btnRepeat;
    private SDL.SDL_Rect _btnAdd;
    private SDL.SDL_Rect _btnAddFolder;
    private SDL.SDL_Rect _btnAddUrl;
    private SDL.SDL_Rect _btnClear;

    // Progress bar bottom buttons
    private SDL.SDL_Rect _btnBarPlaylist;
    private SDL.SDL_Rect _btnBarRepeat;
    private SDL.SDL_Rect _btnBarAbout;

    // About overlay
    public bool AboutVisible = false;

    // Thumbnail preview
    public const int ThumbW = 160;
    public const int ThumbH = 90;
    private IntPtr _thumbTexture = IntPtr.Zero;
    private double _thumbTime = -1;
    public double ProgressHoverTime = -1; // set by Player each frame

    // Pending thumbnail from worker thread — applied on next render from main thread
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

    // Error overlay
    private string? _errorText;
    private long    _errorShowTick;
    private const long ErrorDurationMs = 4000;

    public void ShowError(string message)
    {
        _errorText     = message;
        _errorShowTick = Environment.TickCount64;
    }

    public enum BarButton { None, PlaylistToggle, RepeatMode, About }

    public BarButton HitTestBarButton(int x, int y)
    {
        if (Contains(_btnBarPlaylist, x, y)) return BarButton.PlaylistToggle;
        if (Contains(_btnBarRepeat,   x, y)) return BarButton.RepeatMode;
        if (Contains(_btnBarAbout,    x, y)) return BarButton.About;
        return BarButton.None;
    }

    // Drag-and-drop state (managed by Player, rendered here)
    public int DragFromIndex = -1;
    public int DragToIndex = -1;

    // Context menu state
    public enum ContextMenuAction { None, Play, PlayNext, RemoveFromPlaylist }
    private bool _ctxVisible;
    private int _ctxItemIndex = -1;
    private int _ctxX, _ctxY;
    private const int CtxItemH = 32;
    private const int CtxWidth = 200;
    private static readonly string[] CtxLabels = { "Odtwórz", "Odtwórz jako następny", "Usuń z playlisty" };
    private static readonly ContextMenuAction[] CtxActions = { ContextMenuAction.Play, ContextMenuAction.PlayNext, ContextMenuAction.RemoveFromPlaylist };

    public int ContextMenuTargetItem => _ctxItemIndex;

    public void ShowContextMenu(int x, int y, int itemIndex)
    {
        _ctxVisible = true;
        _ctxItemIndex = itemIndex;
        // Keep menu inside window
        _ctxX = Math.Min(x, _windowW - CtxWidth - 4);
        _ctxY = Math.Min(y, _windowH - CtxLabels.Length * CtxItemH - 8);
    }

    public void HideContextMenu() { _ctxVisible = false; _ctxItemIndex = -1; }

    public ContextMenuAction HitTestContextMenu(int x, int y)
    {
        if (!_ctxVisible) return ContextMenuAction.None;
        int menuH = CtxLabels.Length * CtxItemH;
        if (x < _ctxX || x >= _ctxX + CtxWidth || y < _ctxY || y >= _ctxY + menuH)
        {
            HideContextMenu();
            return ContextMenuAction.None;
        }
        int row = (y - _ctxY) / CtxItemH;
        if (row >= 0 && row < CtxActions.Length)
        {
            HideContextMenu();
            return CtxActions[row];
        }
        return ContextMenuAction.None;
    }

    public bool IsContextMenuVisible => _ctxVisible;

    // Volume bar — triangle shape under control buttons
    // Max width at 150% volume, height proportional
    private const int VolBarMaxW = 240;  // px wide at 150%
    private const int VolBarH    = 36;   // px tall
    private const float VolMax   = 1.5f;

    // No tick-based show — visibility tied to ControlsVisible
    public void ShowVolumeHud() { /* no-op: bar always visible with controls */ }

    // Returns volume value for a click inside the bar rect (both X and Y checked)
    // Returns -1 if not inside the bar
    public float HitTestVolumeBar(int mx, int my)
    {
        if (!ControlsVisible) return -1f;
        int sliderY = _windowH - OverlayBottomMargin;
        int sliderX = _windowW - 40 - VolSliderW;
        if (mx < sliderX || mx > sliderX + VolSliderW) return -1f;
        if (my < sliderY - 12 || my > sliderY + 12) return -1f;
        float frac = (mx - sliderX) / (float)VolSliderW;
        return Math.Clamp(frac * VolMax, 0f, VolMax);
    }

    // Maps only X to volume — used during drag when cursor may leave bar vertically
    public float SampleVolumeBarX(int mx)
    {
        if (!ControlsVisible) return -1f;
        int sliderX = _windowW - 40 - VolSliderW;
        float frac = (mx - sliderX) / (float)VolSliderW;
        return Math.Clamp(frac * VolMax, 0f, VolMax);
    }

    private void DrawVolumeHud()
    {
        if (!ControlsVisible) return;

        int sliderY = _windowH - OverlayBottomMargin;
        int sliderX = _windowW - 40 - VolSliderW;
        float fillFrac = Math.Clamp(Volume / VolMax, 0f, 1f);
        int fillW = (int)(VolSliderW * fillFrac);

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        // Speaker icon (simple shape)
        SDL.SDL_SetRenderDrawColor(_renderer, 200, 200, 200, 200);
        int iconX = sliderX - 22;
        int iconCY = sliderY;
        // Speaker body
        var spkBody = new SDL.SDL_Rect { x = iconX - 4, y = iconCY - 4, w = 6, h = 8 };
        SDL.SDL_RenderFillRect(_renderer, ref spkBody);
        // Speaker cone (triangle)
        for (int dy = -7; dy <= 7; dy++)
        {
            int dx = (int)(4 * (1.0 - (double)Math.Abs(dy) / 7));
            var r = new SDL.SDL_Rect { x = iconX + 2, y = iconCY + dy, w = dx + 1, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }

        // Track background
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 80, 80, 180);
        var trackRect = new SDL.SDL_Rect { x = sliderX, y = sliderY - VolSliderH / 2, w = VolSliderW, h = VolSliderH };
        SDL.SDL_RenderFillRect(_renderer, ref trackRect);

        // Fill
        if (fillW > 0)
        {
            SDL.SDL_SetRenderDrawColor(_renderer, 220, 220, 220, 220);
            var fillRect = new SDL.SDL_Rect { x = sliderX, y = sliderY - VolSliderH / 2, w = fillW, h = VolSliderH };
            SDL.SDL_RenderFillRect(_renderer, ref fillRect);
        }

        // Knob
        int knobR = 5;
        int knobX = sliderX + fillW;
        SDL.SDL_SetRenderDrawColor(_renderer, 240, 240, 240, 230);
        for (int dy = -knobR; dy <= knobR; dy++)
        {
            int dx = (int)Math.Sqrt(knobR * knobR - dy * dy);
            var r = new SDL.SDL_Rect { x = knobX - dx, y = sliderY + dy, w = dx * 2, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }

        // Volume percentage label
        if (_font != IntPtr.Zero)
        {
            string label = $"{(int)(Volume * 100)}%";
            var col = new SDL.SDL_Color { r = 220, g = 220, b = 220, a = 180 };
            IntPtr surf = SDLTtf.TTF_RenderUTF8_Blended(_font, label, col);
            if (surf != IntPtr.Zero)
            {
                IntPtr tex = SDL.SDL_CreateTextureFromSurface(_renderer, surf);
                SDL.SDL_FreeSurface(surf);
                if (tex != IntPtr.Zero)
                {
                    SDL.SDL_QueryTexture(tex, out _, out _, out int tw, out int th);
                    var dst = new SDL.SDL_Rect { x = sliderX + VolSliderW + 6, y = sliderY - th / 2, w = tw, h = th };
                    SDL.SDL_RenderCopy(_renderer, tex, IntPtr.Zero, ref dst);
                    SDL.SDL_DestroyTexture(tex);
                }
            }
        }

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
    }

    private void DrawContextMenu()
    {
        if (!_ctxVisible || _font == IntPtr.Zero) return;
        int menuH = CtxLabels.Length * CtxItemH;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        // Shadow
        SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 90);
        var shadow = new SDL.SDL_Rect { x = _ctxX + 4, y = _ctxY + 4, w = CtxWidth, h = menuH };
        SDL.SDL_RenderFillRect(_renderer, ref shadow);

        // Background
        SDL.SDL_SetRenderDrawColor(_renderer, 28, 28, 36, 245);
        var bg = new SDL.SDL_Rect { x = _ctxX, y = _ctxY, w = CtxWidth, h = menuH };
        SDL.SDL_RenderFillRect(_renderer, ref bg);

        // Border
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 80, 110, 255);
        SDL.SDL_RenderDrawRect(_renderer, ref bg);

        for (int i = 0; i < CtxLabels.Length; i++)
        {
            int itemY = _ctxY + i * CtxItemH;

            // Separator between items
            if (i > 0)
            {
                SDL.SDL_SetRenderDrawColor(_renderer, 55, 55, 75, 180);
                SDL.SDL_RenderDrawLine(_renderer, _ctxX + 8, itemY, _ctxX + CtxWidth - 8, itemY);
            }

            var textColor = CtxActions[i] == ContextMenuAction.RemoveFromPlaylist
                ? new SDL.SDL_Color { r = 220, g = 80, b = 80, a = 255 }
                : new SDL.SDL_Color { r = 210, g = 210, b = 225, a = 255 };

            IntPtr surf = SDLTtf.TTF_RenderUTF8_Blended(_font, CtxLabels[i], textColor);
            if (surf == IntPtr.Zero) continue;
            IntPtr tex = SDL.SDL_CreateTextureFromSurface(_renderer, surf);
            SDL.SDL_FreeSurface(surf);
            if (tex == IntPtr.Zero) continue;
            SDL.SDL_QueryTexture(tex, out _, out _, out int tw, out int th);
            var dst = new SDL.SDL_Rect { x = _ctxX + 14, y = itemY + (CtxItemH - th) / 2, w = tw, h = th };
            SDL.SDL_RenderCopy(_renderer, tex, IntPtr.Zero, ref dst);
            SDL.SDL_DestroyTexture(tex);
        }

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
    }

    // Keyboard-navigation selected index (-1 = none)
    public int PlaylistSelectedIndex = -1;
    // Mouse-hover index (-1 = none)
    public int PlaylistHoverIndex = -1;

    public void ScrollPlaylist(int delta)
    {
        if (_playlistNames.Length == 0 || _windowH <= 0) return;
        int panelH      = _windowH;
        int visibleItems = Math.Max(1, (panelH - PlaylistHeaderH) / PlaylistItemHeight);
        int maxOffset   = Math.Max(0, _playlistNames.Length - visibleItems);
        _playlistScrollOffset = Math.Clamp(_playlistScrollOffset + delta, 0, maxOffset);
    }

    public void ScrollPlaylistToIndex(int index)
    {
        if (index < 0 || _windowH <= 0) return;
        int panelH = _windowH;
        int visibleItems = (panelH - PlaylistHeaderH) / PlaylistItemHeight;
        if (index < _playlistScrollOffset)
            _playlistScrollOffset = index;
        else if (index >= _playlistScrollOffset + visibleItems)
            _playlistScrollOffset = index - visibleItems + 1;
    }

    public enum PlaylistPanelHit { None, RepeatMode, AddFile, AddFolder, AddUrl, ClearPlaylist, ItemClick }
    public int PlaylistPanelClickedItem = -1;

    public PlaylistPanelHit HitTestPlaylistPanel(int x, int y)
    {
        if (!PlaylistPanelVisible) return PlaylistPanelHit.None;
        if (x < 0 || x >= PlaylistPanelWidth) return PlaylistPanelHit.None;
        if (y >= _windowH) return PlaylistPanelHit.None;
        if (Contains(_btnRepeat,    x, y)) return PlaylistPanelHit.RepeatMode;
        if (Contains(_btnAdd,       x, y)) return PlaylistPanelHit.AddFile;
        if (Contains(_btnAddFolder, x, y)) return PlaylistPanelHit.AddFolder;
        if (Contains(_btnAddUrl,    x, y)) return PlaylistPanelHit.AddUrl;
        if (Contains(_btnClear,     x, y)) return PlaylistPanelHit.ClearPlaylist;
        // Item click
        if (y >= 70)
        {
            int idx = _playlistScrollOffset + (y - 70) / PlaylistItemHeight;
            if (idx >= 0 && idx < _playlistNames.Length)
            {
                PlaylistPanelClickedItem = idx;
                return PlaylistPanelHit.ItemClick;
            }
        }
        return PlaylistPanelHit.None;
    }

    private static bool Contains(SDL.SDL_Rect r, int x, int y) =>
        x >= r.x && x < r.x + r.w && y >= r.y && y < r.y + r.h;

    private const int PlaylistHeaderH = 70;

    // Returns item index at given y coordinate in panel (-1 if in header)
    public int GetPlaylistItemIndexAt(int y)
    {
        if (y < PlaylistHeaderH) return -1;
        int idx = _playlistScrollOffset + (y - PlaylistHeaderH) / PlaylistItemHeight;
        if (idx < 0 || idx >= _playlistNames.Length) return -1;
        return idx;
    }

    // Returns drop-target index (insert before this index) from y
    public int GetPlaylistDropIndex(int y)
    {
        if (y < PlaylistHeaderH) return 0;
        int panelH = _windowH;
        if (y >= panelH) return _playlistNames.Length;
        int relY = y - PlaylistHeaderH;
        int idx = _playlistScrollOffset + relY / PlaylistItemHeight;
        int withinItem = relY % PlaylistItemHeight;
        if (withinItem > PlaylistItemHeight / 2) idx++;
        return Math.Clamp(idx, 0, _playlistNames.Length);
    }

    public void SetPlaylistData(string[] names, double[] durations, int currentIndex, double currentTime)
    {
        _playlistNames = names;
        _playlistDurations = durations;
        _playlistCurrentTime = currentTime;
        // Auto-scroll only when the playing track changes — not every frame
        bool trackChanged = currentIndex != _playlistCurrentIndex;
        _playlistCurrentIndex = currentIndex;
        if (trackChanged && currentIndex >= 0 && _windowH > 0)
        {
            int panelH = _windowH;
            int visibleItems = Math.Max(1, (panelH - PlaylistHeaderH) / PlaylistItemHeight);
            if (currentIndex < _playlistScrollOffset)
                _playlistScrollOffset = currentIndex;
            else if (currentIndex >= _playlistScrollOffset + visibleItems)
                _playlistScrollOffset = currentIndex - visibleItems + 1;
        }
    }

    public enum ControlButton { None, Prev, PlayPause, Next, Stop, OpenFile }
    private const int ButtonRadius = 28;
    private const int ButtonSpacing = 80;
    private int _hoveredButton = -1;
    private int[] _overlayButtonXs = [];
    private int _overlayButtonY = 0;

    public void RenderUI()
    {
        ApplyPendingThumbnail();
        SDL.SDL_GetWindowSize(_window, out _windowW, out _windowH);
        if (ProgressBarVisible) DrawProgressBar();
        if (ProgressBarVisible && ProgressHoverTime >= 0) DrawThumbnailPreview();
        if (ControlsVisible) DrawControls();
        if (PlaylistPanelVisible) DrawPlaylistPanel();
        DrawContextMenu();
        if (ControlsVisible) DrawVolumeHud();
        if (AboutVisible) DrawAbout();
        DrawError();
        SDL.SDL_RenderPresent(_renderer);
    }

    public void UpdateVideoFrame(IntPtr yPlane, IntPtr uPlane, IntPtr vPlane, int yStride, int uvStride)
    {
        var rect = new SDL.SDL_Rect { x = 0, y = 0, w = _width, h = _height };
        SDL.SDL_UpdateYUVTexture(_texture, ref rect,
            yPlane, yStride,
            uPlane, uvStride,
            vPlane, uvStride);
        ApplyPendingThumbnail();

        SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 255);
        SDL.SDL_RenderClear(_renderer);

        SDL.SDL_GetWindowSize(_window, out _windowW, out _windowH);

        // Video fills entire window — preserve aspect ratio, center
        double videoAspect = (double)_width / _height;
        double areaAspect = (double)_windowW / _windowH;
        int dstW, dstH, dstX, dstY;
        if (areaAspect > videoAspect)
        {
            dstH = _windowH;
            dstW = (int)(dstH * videoAspect);
            dstX = (_windowW - dstW) / 2;
            dstY = 0;
        }
        else
        {
            dstW = _windowW;
            dstH = (int)(dstW / videoAspect);
            dstX = 0;
            dstY = (_windowH - dstH) / 2;
        }
        var dstRect = new SDL.SDL_Rect { x = dstX, y = dstY, w = dstW, h = dstH };
        SDL.SDL_RenderCopy(_renderer, _texture, IntPtr.Zero, ref dstRect);

        if (ProgressBarVisible)
            DrawProgressBar();
        if (ProgressBarVisible && ProgressHoverTime >= 0)
            DrawThumbnailPreview();
        if (ControlsVisible)
            DrawControls();
        if (PlaylistPanelVisible)
            DrawPlaylistPanel();
        DrawContextMenu();
        if (ControlsVisible)
            DrawVolumeHud();
        if (AboutVisible)
            DrawAbout();
        DrawError();
        UpdateFps();
        DrawFps();
        DrawSubtitle();
        SDL.SDL_RenderPresent(_renderer);
    }

    public void SetProgress(double current) => _progress = current;

    public void GetWindowGeometry(out int x, out int y, out int w, out int h)
    {
        SDL.SDL_GetWindowPosition(_window, out x, out y);
        SDL.SDL_GetWindowSize(_window, out w, out h);
    }

    public void SetWindowGeometry(int x, int y, int w, int h)
    {
        if (x >= 0 && y >= 0) SDL.SDL_SetWindowPosition(_window, x, y);
        if (w > 0 && h > 0) SDL.SDL_SetWindowSize(_window, w, h);
    }

    public void SetFullscreen(bool fullscreen)
    {
        SDL.SDL_SetWindowFullscreen(_window, fullscreen
            ? (uint)SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN_DESKTOP
            : 0);
    }

    public int GetProgressBarY() { SDL.SDL_GetWindowSize(_window, out _windowW, out _windowH); return _windowH - ProgressBarHeight; }
    public int GetWindowWidth() { SDL.SDL_GetWindowSize(_window, out _windowW, out _windowH); return _windowW; }
    public int GetWindowHeight() { SDL.SDL_GetWindowSize(_window, out _windowW, out _windowH); return _windowH; }

    public ControlButton HitTestControl(int x, int y)
    {
        if (!ControlsVisible) return ControlButton.None;
        int hitR = OverlayIconSize / 2 + 6;
        for (int i = 0; i < _overlayButtonXs.Length; i++)
        {
            int dx = x - _overlayButtonXs[i];
            int dy = y - _overlayButtonY;
            if (dx * dx + dy * dy <= hitR * hitR)
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
        // 5 buttons: Prev, PlayPause, Next, Stop, OpenFile — centered on screen
        ControlButton[] buttons = { ControlButton.Prev, ControlButton.PlayPause, ControlButton.Next, ControlButton.Stop, ControlButton.OpenFile };
        int n = buttons.Length;
        int centerX = _windowW / 2;
        int centerY = _windowH / 2;
        int totalW = (n - 1) * OverlayIconSpacing;
        int startX = centerX - totalW / 2;

        _overlayButtonY = centerY;
        _overlayButtonXs = new int[n];
        for (int i = 0; i < n; i++)
            _overlayButtonXs[i] = startX + i * OverlayIconSpacing;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        for (int i = 0; i < n; i++)
        {
            int bx = _overlayButtonXs[i];
            bool hovered = _hoveredButton == i;
            byte bgAlpha = hovered ? (byte)160 : (byte)40;
            byte iconAlpha = hovered ? (byte)255 : (byte)180;
            int r = ButtonRadius;

            // Button circle background — only on hover
            if (hovered)
            {
                SDL.SDL_SetRenderDrawColor(_renderer, 30, 30, 30, bgAlpha);
                for (int dy = -r; dy <= r; dy++)
                {
                    int dx = (int)Math.Sqrt(r * r - dy * dy);
                    var row = new SDL.SDL_Rect { x = bx - dx, y = centerY + dy, w = dx * 2, h = 1 };
                    SDL.SDL_RenderFillRect(_renderer, ref row);
                }

                // Button circle outline (ring with thickness)
                SDL.SDL_SetRenderDrawColor(_renderer, 200, 200, 200, iconAlpha);
                int outlineThickness = 2;
                for (int dy = -r; dy <= r; dy++)
                {
                    int outerDx = (int)Math.Sqrt(r * r - dy * dy);
                    int innerR = r - outlineThickness;
                    int innerDx = innerR > 0 && Math.Abs(dy) <= innerR
                        ? (int)Math.Sqrt(innerR * innerR - dy * dy)
                        : 0;
                    if (outerDx > innerDx)
                    {
                        var r1 = new SDL.SDL_Rect { x = bx - outerDx, y = centerY + dy, w = outerDx - innerDx, h = 1 };
                        SDL.SDL_RenderFillRect(_renderer, ref r1);
                        var r2 = new SDL.SDL_Rect { x = bx + innerDx, y = centerY + dy, w = outerDx - innerDx, h = 1 };
                        SDL.SDL_RenderFillRect(_renderer, ref r2);
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
        SDL.SDL_SetRenderDrawColor(_renderer, 230, 230, 230, alpha);
        int s = 12;
        int offset = 4;
        for (int dy = -s; dy <= s; dy++)
        {
            int halfW = (int)(s * (1.0 - (double)Math.Abs(dy) / s));
            var r = new SDL.SDL_Rect { x = cx - offset, y = cy + dy, w = halfW, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }
    }

    private void DrawPauseIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 230, 230, 230, alpha);
        int barW = 5, barH = 20;
        var left = new SDL.SDL_Rect { x = cx - barW - 1, y = cy - barH / 2, w = barW, h = barH };
        var right = new SDL.SDL_Rect { x = cx + 1, y = cy - barH / 2, w = barW, h = barH };
        SDL.SDL_RenderFillRect(_renderer, ref left);
        SDL.SDL_RenderFillRect(_renderer, ref right);
    }

    private void DrawStopIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 230, 230, 230, alpha);
        int s = 10;
        var r = new SDL.SDL_Rect { x = cx - s, y = cy - s, w = s * 2, h = s * 2 };
        SDL.SDL_RenderFillRect(_renderer, ref r);
    }

    private void DrawPrevIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 230, 230, 230, alpha);
        // Left bar
        var bar = new SDL.SDL_Rect { x = cx - 8, y = cy - 9, w = 3, h = 18 };
        SDL.SDL_RenderFillRect(_renderer, ref bar);
        // Left-pointing triangle (tip on left, base on right)
        int s = 10;
        for (int dy = -s; dy <= s; dy++)
        {
            int halfW = (int)(s * (1.0 - (double)Math.Abs(dy) / s));
            var r = new SDL.SDL_Rect { x = cx + 5 - halfW, y = cy + dy, w = halfW, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }
    }

    private void DrawNextIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 230, 230, 230, alpha);
        // Right-pointing triangle (tip on right, base on left)
        int s = 10;
        for (int dy = -s; dy <= s; dy++)
        {
            int halfW = (int)(s * (1.0 - (double)Math.Abs(dy) / s));
            var r = new SDL.SDL_Rect { x = cx - 5, y = cy + dy, w = halfW, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }
        // Right bar
        var bar = new SDL.SDL_Rect { x = cx + 5, y = cy - 9, w = 3, h = 18 };
        SDL.SDL_RenderFillRect(_renderer, ref bar);
    }

    private void DrawOpenFileIcon(int cx, int cy, byte alpha)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 230, 230, 230, alpha);
        int w = 22, h = 16;
        int fx = cx - w / 2, fy = cy - h / 2;
        var tab = new SDL.SDL_Rect { x = fx, y = fy, w = 8, h = 3 };
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

    private void UpdateFps()
    {
        _fpsFrameCount++;
        long now = Environment.TickCount64;
        if (now - _fpsLastTicks >= 1000)
        {
            _displayedFps = (int)(_fpsFrameCount * 1000.0 / (now - _fpsLastTicks));
            _fpsFrameCount = 0;
            _fpsLastTicks = now;
        }
    }

    private void DrawFps()
    {
        if (_font == IntPtr.Zero) return;
        string fpsText = $"{_displayedFps} FPS";
        var color = new SDL.SDL_Color { r = 180, g = 220, b = 180, a = 200 };
        IntPtr surface = SDLTtf.TTF_RenderUTF8_Blended(_font, fpsText, color);
        if (surface == IntPtr.Zero) return;
        IntPtr tex = SDL.SDL_CreateTextureFromSurface(_renderer, surface);
        SDL.SDL_FreeSurface(surface);
        if (tex == IntPtr.Zero) return;
        SDL.SDL_QueryTexture(tex, out _, out _, out int w, out int h);
        var dst = new SDL.SDL_Rect { x = _windowW - w - 12, y = 8, w = w, h = h };
        SDL.SDL_RenderCopy(_renderer, tex, IntPtr.Zero, ref dst);
        SDL.SDL_DestroyTexture(tex);
    }

    private void DrawPlaylistPanel()
    {
        if (_font == IntPtr.Zero) return;

        int panelH = _windowH;
        int panelX = 0;
        int panelW = PlaylistPanelWidth;

        // Panel background
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
        SDL.SDL_SetRenderDrawColor(_renderer, 15, 15, 20, 210);
        var bg = new SDL.SDL_Rect { x = panelX, y = 0, w = panelW, h = panelH };
        SDL.SDL_RenderFillRect(_renderer, ref bg);

        // Header row 1: title bar
        SDL.SDL_SetRenderDrawColor(_renderer, 35, 35, 50, 255);
        var header = new SDL.SDL_Rect { x = panelX, y = 0, w = panelW, h = 34 };
        SDL.SDL_RenderFillRect(_renderer, ref header);

        // Header row 2: buttons bar
        SDL.SDL_SetRenderDrawColor(_renderer, 25, 25, 38, 255);
        var btnBar = new SDL.SDL_Rect { x = panelX, y = 34, w = panelW, h = 34 };
        SDL.SDL_RenderFillRect(_renderer, ref btnBar);

        // Separator under button bar
        SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 80, 200);
        SDL.SDL_RenderDrawLine(_renderer, panelX, 68, panelX + panelW - 1, 68);

        // Right border
        SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 80, 200);
        SDL.SDL_RenderDrawLine(_renderer, panelX + panelW - 1, 0, panelX + panelW - 1, panelH);

        // Title
        RenderTextInPanel("Playlista", panelW / 2, 9, new SDL.SDL_Color { r = 200, g = 200, b = 220, a = 255 }, center: true);

        // === Repeat mode button ===
        string repeatLabel = PlaylistRepeatMode switch
        {
            RepeatMode.All     => "[All]",
            RepeatMode.One     => "[One]",
            RepeatMode.Shuffle => "[Shuffle]",
            _                  => "[Once]",
        };
        var repeatColor = PlaylistRepeatMode == RepeatMode.Once
            ? new SDL.SDL_Color { r = 140, g = 140, b = 160, a = 220 }
            : new SDL.SDL_Color { r = 100, g = 180, b = 255, a = 255 };

        // Measure repeat label width for button sizing
        int repeatLabelW = 90;
        if (_font != IntPtr.Zero)
            SDLTtf.TTF_SizeUTF8(_font, repeatLabel, out repeatLabelW, out _);

        _btnRepeat = new SDL.SDL_Rect { x = panelX + 6, y = 37, w = repeatLabelW + 16, h = 26 };
        // Button background
        SDL.SDL_SetRenderDrawColor(_renderer, 40, 50, 70, 180);
        SDL.SDL_RenderFillRect(_renderer, ref _btnRepeat);
        SDL.SDL_SetRenderDrawColor(_renderer, 70, 90, 120, 200);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnRepeat);
        RenderTextInPanel(repeatLabel, _btnRepeat.x + 8, _btnRepeat.y + 5, repeatColor);

        // === Add URL button (🔗) ===
        _btnAddUrl = new SDL.SDL_Rect { x = panelX + panelW - 144, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 70, 40, 90, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnAddUrl);
        SDL.SDL_SetRenderDrawColor(_renderer, 150, 90, 200, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnAddUrl);
        RenderTextInPanel("URL", _btnAddUrl.x + 3, _btnAddUrl.y + 4,
            new SDL.SDL_Color { r = 210, g = 170, b = 255, a = 255 });

        // === Clear playlist button (🗑) ===
        _btnClear = new SDL.SDL_Rect { x = panelX + panelW - 108, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 30, 30, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnClear);
        SDL.SDL_SetRenderDrawColor(_renderer, 160, 60, 60, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnClear);
        RenderTextInPanel("CLR", _btnClear.x + 4, _btnClear.y + 4,
            new SDL.SDL_Color { r = 255, g = 120, b = 120, a = 255 });

        // === Add folder button (📁) ===
        _btnAddFolder = new SDL.SDL_Rect { x = panelX + panelW - 72, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 50, 60, 100, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnAddFolder);
        SDL.SDL_SetRenderDrawColor(_renderer, 90, 110, 180, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnAddFolder);
        RenderTextInPanel("DIR", _btnAddFolder.x + 3, _btnAddFolder.y + 4,
            new SDL.SDL_Color { r = 160, g = 180, b = 255, a = 255 });

        // === Add file button (+) ===
        _btnAdd = new SDL.SDL_Rect { x = panelX + panelW - 36, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 40, 80, 50, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnAdd);
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 90, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnAdd);
        RenderTextInPanel("+", _btnAdd.x + _btnAdd.w / 2, _btnAdd.y + 4,
            new SDL.SDL_Color { r = 140, g = 230, b = 140, a = 255 }, center: true);

        int headerH = 70;
        int visibleItems = (panelH - headerH) / PlaylistItemHeight;
        int maxOffset = Math.Max(0, _playlistNames.Length - visibleItems);
        _playlistScrollOffset = Math.Clamp(_playlistScrollOffset, 0, maxOffset);

        for (int i = _playlistScrollOffset; i < _playlistNames.Length && i < _playlistScrollOffset + visibleItems; i++)
        {
            int itemY = headerH + (i - _playlistScrollOffset) * PlaylistItemHeight;
            bool isCurrent  = i == _playlistCurrentIndex;
            bool isDragging = i == DragFromIndex && DragFromIndex >= 0;
            bool isSelected = i == PlaylistSelectedIndex && PlaylistSelectedIndex >= 0;
            bool isHovered  = i == PlaylistHoverIndex && PlaylistHoverIndex >= 0 && !isCurrent;

            // Dragged item: dim it
            if (isDragging)
            {
                SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 60, 120);
                var dimBg = new SDL.SDL_Rect { x = panelX, y = itemY, w = panelW, h = PlaylistItemHeight };
                SDL.SDL_RenderFillRect(_renderer, ref dimBg);
            }

            // Hover highlight
            if (isHovered && !isDragging)
            {
                SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
                SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, 18);
                var hovBg = new SDL.SDL_Rect { x = panelX, y = itemY, w = panelW, h = PlaylistItemHeight };
                SDL.SDL_RenderFillRect(_renderer, ref hovBg);
                SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
            }

            // Item background
            if (isCurrent && !isDragging)
            {
                SDL.SDL_SetRenderDrawColor(_renderer, 180, 90, 10, 200);
                var itemBg = new SDL.SDL_Rect { x = panelX + 2, y = itemY + 1, w = panelW - 4, h = PlaylistItemHeight - 2 };
                SDL.SDL_RenderFillRect(_renderer, ref itemBg);
                // Left orange accent bar
                SDL.SDL_SetRenderDrawColor(_renderer, 255, 140, 0, 255);
                var accent = new SDL.SDL_Rect { x = panelX, y = itemY + 1, w = 3, h = PlaylistItemHeight - 2 };
                SDL.SDL_RenderFillRect(_renderer, ref accent);
            }

            // Keyboard selection: white outline rectangle
            if (isSelected && !isDragging)
            {
                SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, 200);
                var sel = new SDL.SDL_Rect { x = panelX + 2, y = itemY + 1, w = panelW - 4, h = PlaylistItemHeight - 2 };
                SDL.SDL_RenderDrawRect(_renderer, ref sel);
            }

            // Separator line
            SDL.SDL_SetRenderDrawColor(_renderer, 45, 45, 60, 150);
            SDL.SDL_RenderDrawLine(_renderer, panelX + 8, itemY + PlaylistItemHeight - 1, panelX + panelW - 8, itemY + PlaylistItemHeight - 1);

            int textY = itemY + (PlaylistItemHeight - 18) / 2;

            // Build time string
            string timeStr;
            if (isCurrent)
            {
                double dur = i < _playlistDurations.Length ? _playlistDurations[i] : 0;
                timeStr = $"{FormatTime(_playlistCurrentTime)}/{FormatTime(dur)}";
            }
            else
            {
                double dur = i < _playlistDurations.Length ? _playlistDurations[i] : 0;
                timeStr = FormatTime(dur);
            }

            // Right-side time
            var timeColor = isCurrent
                ? new SDL.SDL_Color { r = 140, g = 200, b = 255, a = 220 }
                : new SDL.SDL_Color { r = 120, g = 120, b = 140, a = 180 };
            IntPtr timeSurf = SDLTtf.TTF_RenderUTF8_Blended(_font, timeStr, timeColor);
            int timeW = 0, timeH = 0;
            if (timeSurf != IntPtr.Zero)
            {
                IntPtr timeTex = SDL.SDL_CreateTextureFromSurface(_renderer, timeSurf);
                SDL.SDL_FreeSurface(timeSurf);
                if (timeTex != IntPtr.Zero)
                {
                    SDL.SDL_QueryTexture(timeTex, out _, out _, out timeW, out timeH);
                    var timeDst = new SDL.SDL_Rect { x = panelX + panelW - timeW - 10, y = textY, w = timeW, h = timeH };
                    SDL.SDL_RenderCopy(_renderer, timeTex, IntPtr.Zero, ref timeDst);
                    SDL.SDL_DestroyTexture(timeTex);
                }
            }

            // Left-side track name (truncated to fit)
            string name = _playlistNames[i];
            int maxNameW = panelW - timeW - 28;
            var nameColor = isCurrent
                ? new SDL.SDL_Color { r = 255, g = 220, b = 140, a = 255 }
                : new SDL.SDL_Color { r = 180, g = 180, b = 195, a = 220 };
            name = TruncateText(name, maxNameW);
            IntPtr nameSurf = SDLTtf.TTF_RenderUTF8_Blended(_font, name, nameColor);
            if (nameSurf != IntPtr.Zero)
            {
                IntPtr nameTex = SDL.SDL_CreateTextureFromSurface(_renderer, nameSurf);
                SDL.SDL_FreeSurface(nameSurf);
                if (nameTex != IntPtr.Zero)
                {
                    SDL.SDL_QueryTexture(nameTex, out _, out _, out int nw, out int nh);
                    var nameDst = new SDL.SDL_Rect { x = panelX + 10, y = textY, w = nw, h = nh };
                    SDL.SDL_RenderCopy(_renderer, nameTex, IntPtr.Zero, ref nameDst);
                    SDL.SDL_DestroyTexture(nameTex);
                }
            }
        }

        // Drop indicator: bright orange line at drop position
        if (DragFromIndex >= 0 && DragToIndex >= 0)
        {
            int dropPos = DragToIndex - _playlistScrollOffset;
            int lineY = headerH + dropPos * PlaylistItemHeight;
            lineY = Math.Clamp(lineY, headerH, panelH - 2);
            SDL.SDL_SetRenderDrawColor(_renderer, 255, 140, 0, 255);
            for (int t = 0; t < 3; t++)
                SDL.SDL_RenderDrawLine(_renderer, panelX + 4, lineY + t, panelX + panelW - 4, lineY + t);
        }

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
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

            // Black outline/shadow for readability
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

    private void DrawProgressBar()
    {
        int barY = _windowH - ProgressBarHeight;
        int margin = 20;
        int barW = _windowW - margin * 2;
        int barX = margin;
        int trackH = TrackHeight;
        int trackY = _windowH - trackH - 4;

        // Semi-transparent gradient overlay at bottom
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
        // Draw gradient: more opaque at bottom, fading upward
        for (int i = 0; i < ProgressBarHeight; i++)
        {
            byte alpha = (byte)(180 * (i / (double)ProgressBarHeight));
            SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, alpha);
            var row = new SDL.SDL_Rect { x = 0, y = barY + i, w = _windowW, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref row);
        }

        // Text row: title (left) and time (right) above the track
        DrawProgressBarText(barY, barX, barW);

        // Track — semi-transparent
        SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 60, 180);
        var trackRect = new SDL.SDL_Rect { x = barX, y = trackY, w = barW, h = trackH };
        SDL.SDL_RenderFillRect(_renderer, ref trackRect);

        // Fill — blue accent
        double frac = _duration > 0 ? Math.Clamp(_progress / _duration, 0, 1) : 0;
        int fillW = (int)(barW * frac);
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 255, 230);
        if (fillW > 0)
        {
            var fillRect = new SDL.SDL_Rect { x = barX, y = trackY, w = fillW, h = trackH };
            SDL.SDL_RenderFillRect(_renderer, ref fillRect);
        }

        // Knob — small, only visible when hovered or dragging
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

        // === Bottom-bar buttons (left side) ===
        int bx = barX;

        // [☰] Playlist toggle
        _btnBarPlaylist = new SDL.SDL_Rect { x = bx, y = btnY, w = BarBtnW, h = BarBtnH };
        var plCol = PlaylistPanelVisible ? active : white;
        DrawBarButtonBg(bx, btnY, PlaylistPanelVisible);
        RenderText("PL", bx + 3, btnY + 3, plCol);
        bx += BarBtnW + 4;

        // [↺] Repeat mode
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

        // Left: title — start after buttons
        string titleText = TruncateText(_title, (barX + barW / 2) - bx - 4);
        RenderText(titleText, bx, textY, white);

        // Right: [?] About button then time
        int rx = barX + barW;
        rx -= BarBtnW + 2;
        _btnBarAbout = new SDL.SDL_Rect { x = rx, y = btnY, w = BarBtnW, h = BarBtnH };
        DrawBarButtonBg(rx, btnY, AboutVisible);
        var aboutCol = AboutVisible ? active : white;
        RenderText("?", rx + 8, btnY + 3, aboutCol);

        // Time text left of about button
        string timeText = $"{FormatTime(_progress)} / {FormatTime(_duration)}";
        SDLTtf.TTF_SizeUTF8(_font, timeText, out int tw, out int _);
        RenderText(timeText, rx - tw - 8, textY, white);
    }

    private void DrawThumbnailPreview()
    {
        int margin = 20;
        int barW   = _windowW - margin * 2;
        int barX   = margin;
        int trackY = _windowH - TrackHeight - 4;

        if (_duration <= 0) return;
        double frac = Math.Clamp(ProgressHoverTime / _duration, 0, 1);
        int cx = barX + (int)(barW * frac);

        int pad  = 4;
        int tw   = ThumbW + pad * 2;
        int th   = ThumbH + pad * 2 + (_font != IntPtr.Zero ? 18 : 0);
        int tx   = Math.Clamp(cx - tw / 2, 0, _windowW - tw);
        int ty   = trackY - th - 80;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        // Shadow
        SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 100);
        var shadow = new SDL.SDL_Rect { x = tx + 3, y = ty + 3, w = tw, h = th };
        SDL.SDL_RenderFillRect(_renderer, ref shadow);

        // Background
        SDL.SDL_SetRenderDrawColor(_renderer, 20, 20, 24, 230);
        var bg = new SDL.SDL_Rect { x = tx, y = ty, w = tw, h = th };
        SDL.SDL_RenderFillRect(_renderer, ref bg);

        // Border
        SDL.SDL_SetRenderDrawColor(_renderer, 180, 180, 180, 160);
        SDL.SDL_RenderDrawRect(_renderer, ref bg);
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);

        // Thumbnail image (if available and time matches)
        if (_thumbTexture != IntPtr.Zero && Math.Abs(_thumbTime - ProgressHoverTime) < 3.0)
        {
            var imgDst = new SDL.SDL_Rect { x = tx + pad, y = ty + pad, w = ThumbW, h = ThumbH };
            SDL.SDL_RenderCopy(_renderer, _thumbTexture, IntPtr.Zero, ref imgDst);
        }
        else
        {
            // Placeholder while loading
            SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
            SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 70, 200);
            var ph = new SDL.SDL_Rect { x = tx + pad, y = ty + pad, w = ThumbW, h = ThumbH };
            SDL.SDL_RenderFillRect(_renderer, ref ph);
            SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
        }

        // Time label
        if (_font != IntPtr.Zero)
        {
            string label = FormatTime(ProgressHoverTime);
            var white = new SDL.SDL_Color { r = 220, g = 220, b = 220, a = 255 };
            SDLTtf.TTF_SizeUTF8(_font, label, out int lw, out _);
            RenderText(label, tx + (tw - lw) / 2, ty + pad + ThumbH + 2, white);
        }

        // Stem line to trackDevin-linux-x64-3.3.1018+next.16737566f5
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
        SDL.SDL_SetRenderDrawColor(_renderer, 200, 200, 200, 120);
        SDL.SDL_RenderDrawLine(_renderer, cx, ty + th, cx, trackY);
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
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

    private void DrawAbout()
    {
        if (_font == IntPtr.Zero) return;

        int panW = 420, panH = 220;
        int panX = (_windowW - panW) / 2;
        int panY = (_windowH - panH) / 2;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        // Shadow
        SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 120);
        var shadow = new SDL.SDL_Rect { x = panX + 4, y = panY + 4, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref shadow);

        // Background
        SDL.SDL_SetRenderDrawColor(_renderer, 28, 28, 32, 235);
        var bg = new SDL.SDL_Rect { x = panX, y = panY, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref bg);

        // Border
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

        // Close hint
        var hint = new SDL.SDL_Color { r = 100, g = 100, b = 100, a = 255 };
        RenderText("[?] lub ESC aby zamknąć", panX + panW - 190, panY + panH - 22, hint);
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

    public void StopAudio()
    {
        if (_audioInitialized)
        {
            SDL.SDL_PauseAudio(1);
            SDL.SDL_CloseAudio();
            _audioInitialized = false;
            Log("Audio stopped");
        }
    }

    public void ReinitForNewFile(int width, int height, double duration, string title)
    {
        Log($"ReinitForNewFile: {title} {width}x{height} dur={duration:F1}s");
        StopAudio();

        // Destroy old texture if dimensions changed
        if (_texture != IntPtr.Zero && (width != _width || height != _height))
        {
            SDL.SDL_DestroyTexture(_texture);
            _texture = IntPtr.Zero;
            _width = width;
            _height = height;
        }
        else if (_texture == IntPtr.Zero)
        {
            _width = width;
            _height = height;
        }

        // Re-create texture
        if (_renderer != IntPtr.Zero && _width > 0 && _height > 0)
        {
            if (_texture == IntPtr.Zero)
            {
                _texture = SDL.SDL_CreateTexture(_renderer,
                    SDL.SDL_PIXELFORMAT_IYUV,
                    (int)SDL.SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING,
                    _width, _height);
                Log($"Texture re-created {_width}x{_height}");
            }
        }

        _duration = duration;
        _title = title;
        _progress = 0;
        _subtitleText = null;
        _playlistScrollOffset = 0;
    }

    private static void Log(string msg) =>
        Console.Error.WriteLine($"[Renderer] {msg}");

    public void Dispose()
    {
        StopAudio();
        if (_font != IntPtr.Zero) { SDLTtf.TTF_CloseFont(_font); _font = IntPtr.Zero; }
        if (_subtitleFont != IntPtr.Zero) { SDLTtf.TTF_CloseFont(_subtitleFont); _subtitleFont = IntPtr.Zero; }
        if (_ttfInit) { SDLTtf.TTF_Quit(); _ttfInit = false; }
        if (_thumbTexture != IntPtr.Zero) { SDL.SDL_DestroyTexture(_thumbTexture); _thumbTexture = IntPtr.Zero; }
        if (_texture != IntPtr.Zero) { SDL.SDL_DestroyTexture(_texture); _texture = IntPtr.Zero; }
        if (_renderer != IntPtr.Zero) { SDL.SDL_DestroyRenderer(_renderer); _renderer = IntPtr.Zero; }
        if (_window != IntPtr.Zero) { SDL.SDL_DestroyWindow(_window); _window = IntPtr.Zero; }
        SDL.SDL_Quit();
        Log("Disposed");
    }
}
