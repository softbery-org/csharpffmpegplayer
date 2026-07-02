using SDL2;
using System.Runtime.InteropServices;
using Subtitles;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer : IDisposable
{
    private IntPtr _window;
    private IntPtr _renderer;
    private IntPtr _texture;
    private int _width;
    private int _height;
    private bool _audioInitialized;
    private IntPtr _cursorDefault;
    private IntPtr _cursorSizeWE;
    private IntPtr _cursorSizeNS;
    private IntPtr _cursorSizeNWSE;
    private IntPtr _cursorHand;
    private SDL.SDL_AudioCallback? _audioCallbackDelegate;
    private Action<byte[]>? _userAudioCallback;
    private int _windowW;
    private int _windowH;
    private int _windowedX;
    private int _windowedY;
    private int _windowedW;
    private int _windowedH;
    private bool _windowedGeometrySaved;
    private bool _windowWasMaximized;
    private const int ProgressBarHeight = 60;
    private const int ProgressBarBottomMargin = 28;
    private const int TextHeight = 18;
    private const int TrackHeight = 4;
    private const int BarBtnH = 22;
    private const int BarBtnW = 28;
    private const int OverlayBottomMargin = 28;
    private const int VolBarCount = 20;
    private const int VolBarW = 4;
    private const int VolBarGap = 2;
    private const int VolSliderW = VolBarCount * VolBarW + (VolBarCount - 1) * VolBarGap;
    private const int VolSliderH = 22;
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

    public bool ProgressBarVisible = true;
    public bool ControlsVisible = true;
    public bool IsPlaying = true;
    public bool PlaylistPanelVisible = false;
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
    private SDL.SDL_Rect _btnServer;
    private SDL.SDL_Rect _btnDisconnect;
    private SDL.SDL_Rect _btnFilter;
    private SDL.SDL_Rect _btnClear;

    // Progress bar bottom buttons
    private SDL.SDL_Rect _btnBarPlaylist;
    private SDL.SDL_Rect _btnBarRepeat;
    private SDL.SDL_Rect _btnBarAbout;

    // Drag-and-drop state (managed by Player, rendered here)
    public int DragFromIndex = -1;
    public int DragToIndex = -1;

    // Title bar buttons
    private bool _minimizeHovered;
    private bool _maximizeHovered;
    private bool _closeHovered;

    // Control buttons
    private const int ButtonRadius = 28;
    private const int ButtonSpacing = 80;
    private int _hoveredButton = -1;
    private int[] _overlayButtonXs = [];
    private int _overlayButtonY = 0;

    public enum BarButton { None, PlaylistToggle, RepeatMode, About }

    public BarButton HitTestBarButton(int x, int y)
    {
        if (Contains(_btnBarPlaylist, x, y)) return BarButton.PlaylistToggle;
        if (Contains(_btnBarRepeat,   x, y)) return BarButton.RepeatMode;
        if (Contains(_btnBarAbout,    x, y)) return BarButton.About;
        return BarButton.None;
    }

    public void InitVideo(int width, int height)
    {
        _width = width;
        _height = height;
        SDL.SDL_Init(SDL.SDL_INIT_VIDEO);
        SDL.SDL_SetHint(SDL.SDL_HINT_RENDER_SCALE_QUALITY, "1");
        _window = SDL.SDL_CreateWindow(
            "CSharp FFmpeg Player",
            SDL.SDL_WINDOWPOS_CENTERED, SDL.SDL_WINDOWPOS_CENTERED,
            width, height, SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN | SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL.SDL_WindowFlags.SDL_WINDOW_BORDERLESS);
        SDL.SDL_GetWindowSize(_window, out _windowW, out _windowH);
        _renderer = SDL.SDL_CreateRenderer(_window, -1,
            SDL.SDL_RendererFlags.SDL_RENDERER_ACCELERATED | SDL.SDL_RendererFlags.SDL_RENDERER_PRESENTVSYNC);
        _texture = SDL.SDL_CreateTexture(_renderer,
            SDL.SDL_PIXELFORMAT_IYUV,
            (int)SDL.SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING,
            width, height);

        InitFont();
        InitCursors();
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

    public void RenderUI()
    {
        DrawTitleBarButtons();
        ApplyPendingThumbnail();
        SDL.SDL_GetWindowSize(_window, out _windowW, out _windowH);
        if (ProgressBarVisible) DrawProgressBar();
        if (ProgressBarVisible && ProgressHoverTime >= 0) DrawThumbnailPreview();
        if (ControlsVisible) DrawControls();
        if (PlaylistPanelVisible) DrawPlaylistPanel();
        DrawContextMenu();
        if (ControlsVisible) DrawVolumeHud();
        if (AboutVisible) DrawAbout();
        if (LoginVisible) DrawLogin();
        if (SeriesInfoVisible) DrawSeriesInfo();
        if (EpisodeInfoVisible) DrawEpisodeInfo();
        DrawError();
        DrawStatus();
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

        DrawTitleBarButtons();

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
        if (LoginVisible)
            DrawLogin();
        if (SeriesInfoVisible)
            DrawSeriesInfo();
        if (EpisodeInfoVisible)
            DrawEpisodeInfo();
        DrawError();
        DrawStatus();
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
        if (fullscreen)
        {
            // Save windowed geometry before entering fullscreen
            var flags = (SDL.SDL_WindowFlags)SDL.SDL_GetWindowFlags(_window);
            _windowWasMaximized = flags.HasFlag(SDL.SDL_WindowFlags.SDL_WINDOW_MAXIMIZED);
            if (!_windowWasMaximized)
            {
                SDL.SDL_GetWindowPosition(_window, out _windowedX, out _windowedY);
                SDL.SDL_GetWindowSize(_window, out _windowedW, out _windowedH);
                _windowedGeometrySaved = true;
            }
            else
            {
                _windowedGeometrySaved = false;
            }
            SDL.SDL_SetWindowFullscreen(_window, (uint)SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN_DESKTOP);
        }
        else
        {
            SDL.SDL_SetWindowFullscreen(_window, 0);
            if (_windowedGeometrySaved)
            {
                SDL.SDL_SetWindowSize(_window, _windowedW, _windowedH);
                SDL.SDL_SetWindowPosition(_window, _windowedX, _windowedY);
            }
            else if (_windowWasMaximized)
            {
                SDL.SDL_MaximizeWindow(_window);
            }
        }
    }

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_SetWindowAlwaysOnTop(IntPtr window, SDL.SDL_bool on_top);

    public bool AlwaysOnTop { get; private set; }

    public void SetAlwaysOnTop(bool onTop)
    {
        AlwaysOnTop = onTop;
        if (_window != IntPtr.Zero)
            SDL_SetWindowAlwaysOnTop(_window, onTop ? SDL.SDL_bool.SDL_TRUE : SDL.SDL_bool.SDL_FALSE);
    }

    public int GetWindowWidth() { SDL.SDL_GetWindowSize(_window, out _windowW, out _windowH); return _windowW; }
    public int GetWindowHeight() { SDL.SDL_GetWindowSize(_window, out _windowW, out _windowH); return _windowH; }

    private const int ResizeBorderWidth = 6;

    public enum ResizeEdge { None, Right, Bottom, BottomRight }

    public ResizeEdge HitTestResize(int x, int y)
    {
        SDL.SDL_GetWindowSize(_window, out _windowW, out _windowH);
        bool onRight = x >= _windowW - ResizeBorderWidth && x < _windowW;
        bool onBottom = y >= _windowH - ResizeBorderWidth && y < _windowH;
        if (onRight && onBottom) return ResizeEdge.BottomRight;
        if (onRight) return ResizeEdge.Right;
        if (onBottom) return ResizeEdge.Bottom;
        return ResizeEdge.None;
    }

    private void InitCursors()
    {
        _cursorDefault = SDL.SDL_CreateSystemCursor(SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_ARROW);
        _cursorSizeWE = SDL.SDL_CreateSystemCursor(SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_SIZEWE);
        _cursorSizeNS = SDL.SDL_CreateSystemCursor(SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_SIZENS);
        _cursorSizeNWSE = SDL.SDL_CreateSystemCursor(SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_SIZENWSE);
        _cursorHand = SDL.SDL_CreateSystemCursor(SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_HAND);
    }

    public void UpdateCursor(int x, int y)
    {
        IntPtr target = _cursorDefault;

        var edge = HitTestResize(x, y);
        if (edge == ResizeEdge.Right) target = _cursorSizeWE;
        else if (edge == ResizeEdge.Bottom) target = _cursorSizeNS;
        else if (edge == ResizeEdge.BottomRight) target = _cursorSizeNWSE;
        else if (HitTestTitleBar(x, y) != TitleBarButton.None) target = _cursorHand;

        if (target != IntPtr.Zero)
            SDL.SDL_SetCursor(target);
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

        const int fpsTextY = 6;
        string fpsText = $"{_displayedFps} FPS";
        var color = new SDL.SDL_Color { r = 180, g = 220, b = 180, a = 200 };
        IntPtr surface = SDLTtf.TTF_RenderUTF8_Blended(_font, fpsText, color);
        if (surface == IntPtr.Zero) return;
        IntPtr tex = SDL.SDL_CreateTextureFromSurface(_renderer, surface);
        SDL.SDL_FreeSurface(surface);
        if (tex == IntPtr.Zero) return;
        SDL.SDL_QueryTexture(tex, out _, out _, out int w, out int h);
        // Keep FPS text away from title bar logo/buttons
        int x = ControlsVisible ? 40 : 10;
        var dst = new SDL.SDL_Rect { x = x, y = fpsTextY, w = w, h = h };
        SDL.SDL_RenderCopy(_renderer, tex, IntPtr.Zero, ref dst);
        SDL.SDL_DestroyTexture(tex);
    }

    public void ReinitForNewFile(int width, int height, double duration, string title)
    {
        Log($"ReinitForNewFile: {title} {width}x{height} dur={duration:F1}s");
        StopAudio();

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
        if (_cursorDefault != IntPtr.Zero) { SDL.SDL_FreeCursor(_cursorDefault); _cursorDefault = IntPtr.Zero; }
        if (_cursorSizeWE != IntPtr.Zero) { SDL.SDL_FreeCursor(_cursorSizeWE); _cursorSizeWE = IntPtr.Zero; }
        if (_cursorSizeNS != IntPtr.Zero) { SDL.SDL_FreeCursor(_cursorSizeNS); _cursorSizeNS = IntPtr.Zero; }
        if (_cursorSizeNWSE != IntPtr.Zero) { SDL.SDL_FreeCursor(_cursorSizeNWSE); _cursorSizeNWSE = IntPtr.Zero; }
        if (_cursorHand != IntPtr.Zero) { SDL.SDL_FreeCursor(_cursorHand); _cursorHand = IntPtr.Zero; }
        SDL.SDL_Quit();
        Log("Disposed");
    }
}
