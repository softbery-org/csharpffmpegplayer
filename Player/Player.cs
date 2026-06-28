using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SDL2;
using Subtitles;

namespace CSharpFFmpeg;

public sealed partial class Player : IDisposable
{
    private readonly FFmpegDecoder _decoder = new();
    private SDLRenderer _renderer = new();
    private Thread? _decodeThread;

    private readonly object _audioLock = new();
    private readonly Queue<byte[]> _audioQueue = new();
    private const int MaxAudioQueueBytes = 4_000_000;
    private byte[]? _audioPartial;
    private int _audioPartialOffset;

    private readonly object _videoLock = new();
    private readonly Queue<VideoFrame> _videoQueue = new();
    private const int MaxVideoQueue = 30;

    private volatile bool _running = true;
    private volatile bool _decodeRunning = false;
    private volatile bool _trackEof = false;
    private volatile bool _paused = false;
    private double _audioPts;
    private readonly Stopwatch _clock = new();
    private double _clockBasePts;
    private bool _clockStarted;

    private int _videoWidth;
    private int _videoHeight;
    private double _duration;
    private volatile bool _seekRequested;
    private double _seekTarget;
    private bool _mouseDragging;
    private bool _fullscreen;
    private bool _windowDragging;
    private int _windowDragOffsetX;
    private int _windowDragOffsetY;
    private bool _windowResizing;
    private int _resizeEdge; // 0=none, 1=right, 2=bottom, 3=bottom-right
    private int _resizeStartWinX, _resizeStartWinY;
    private int _resizeStartW, _resizeStartH;
    private int _plDragFromIndex = -1;
    private bool _plDragging;
    private bool _volDragging;
    private int _mouseX, _mouseY;
    private long _lastClickTicks;
    private const long DoubleClickMs = 400;
    private int _targetFps;
    private long _lastMouseMotionTicks;
    private const long AutoHideMs = 7000;
    private bool _uiHidden;
    private int _hoveredButton = -1;
    private string? _pendingOpenFile;
    private int _pendingPrevNext;
    private volatile bool _stopRequested;
    private bool _forceRedraw;
    private VideoFrame? _lastFrame;
    private bool _stopped;
    private bool _trackEnded;
    private long _trackOpenTicks;
    private long _lastReopenTicks;

    // Thumbnail preview
    private long   _thumbRequestBits = unchecked((long)0xFFF8000000000000L);
    private Thread? _thumbThread;
    private readonly System.Threading.ManualResetEventSlim _thumbSignal = new(false);

    public bool UseHwAccel { set { _useHwAccel = value; _decoder.UseHwAccel = value; } }
    public int TargetFps { set => _targetFps = value; }
    public string? SubtitlePath { get; set; }
    public Playlist? Playlist { get; set; }
    public double StartPositionSec { get; set; } = 0;
    public float RestoreVolume { get; set; } = 1.0f;
    public bool RestorePlaylistVisible { get; set; } = false;
    public int RestoreWinX { get; set; } = -1;
    public int RestoreWinY { get; set; } = -1;
    public int RestoreWinW { get; set; } = 0;
    public int RestoreWinH { get; set; } = 0;

    private bool _useHwAccel;
    private SubtitleManager? _subtitles;
    private string _videoPath = "";

    public void SaveSession()
    {
        if (Playlist == null || Playlist.Count == 0) return;
        double pos = GetMasterClock();
        _renderer.GetWindowGeometry(out int wx, out int wy, out int ww, out int wh);
        SessionManager.Save(Playlist, pos, _renderer.Volume,
            _renderer.PlaylistPanelVisible, wx, wy, ww, wh);
    }

    public void Play(string filePath, PlaylistEntry? entry = null)
    {
        _videoPath = filePath;
        try
        {
            _decoder.UseHwAccel = _useHwAccel;
            _decoder.Open(filePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Player] Initial open failed: {ex.Message}");
            if (entry != null && !string.IsNullOrEmpty(entry.SourceUrl))
            {
                var plugin = PluginLoader.FindPluginForUrl(entry.SourceUrl);
                if (plugin != null)
                {
                    Console.Error.WriteLine($"[Player] Re-extracting from SourceUrl: {entry.SourceUrl}");
                    var newEntries = plugin.Resolve(entry.SourceUrl);
                    if (newEntries.Count > 0)
                    {
                        entry.Url = newEntries[0].Url;
                        if (!string.IsNullOrEmpty(newEntries[0].DisplayName))
                            entry.DisplayName = newEntries[0].DisplayName;
                        _videoPath = entry.Url;
                        _decoder.Open(entry.Url);
                        Console.Error.WriteLine($"[Player] Re-opened with fresh URL");
                    }
                    else throw;
                }
                else throw;
            }
            else throw;
        }

        _videoWidth = _decoder.VideoWidth;
        _videoHeight = _decoder.VideoHeight;
        _duration = _decoder.DurationSec;

        if (_decoder.VideoStreamIndex >= 0)
        {
            _renderer.InitVideo(_videoWidth, _videoHeight);
            _renderer.Duration = _duration;
            _renderer.Title = Path.GetFileNameWithoutExtension(filePath);
        }

        TryLoadSubtitles(filePath);

        if (_decoder.AudioStreamIndex >= 0)
            _renderer.InitAudio(_decoder.SampleRate, 2, OnAudioCallback);

        _renderer.Volume = RestoreVolume;
        _renderer.PlaylistPanelVisible = RestorePlaylistVisible;
        if (RestoreWinW > 0)
            _renderer.SetWindowGeometry(RestoreWinX, RestoreWinY, RestoreWinW, RestoreWinH);

        _decodeRunning = true;
        _trackEof = false;
        _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "Decode" };
        _decodeThread.Start();
        _trackOpenTicks = Environment.TickCount64;

        if (StartPositionSec > 1.0)
        {
            Console.Error.WriteLine($"[Player] Restoring position {StartPositionSec:F1}s");
            Thread.Sleep(300);
            RequestSeek(StartPositionSec);
            StartPositionSec = 0;
        }

        EventLoop();
    }

    public void Dispose()
    {
        _running = false;
        _decodeThread?.Join(2000);
        lock (_videoLock) Monitor.Pulse(_videoLock);
        _renderer.Dispose();
        _decoder.Dispose();
    }

    private readonly struct VideoFrame : IDisposable
    {
        public readonly double Pts;
        public readonly IntPtr YPlane;
        public readonly IntPtr UPlane;
        public readonly IntPtr VPlane;
        public readonly int YStride;
        public readonly int UVStride;

        public VideoFrame(double pts, IntPtr y, IntPtr u, IntPtr v, int yStride, int uvStride)
        {
            Pts = pts;
            YPlane = y;
            UPlane = u;
            VPlane = v;
            YStride = yStride;
            UVStride = uvStride;
        }

        public static VideoFrame FromDecoder(FFmpegDecoder dec, int width, int height)
        {
            int yStride = width;
            int uvStride = width / 2;
            int ySize = yStride * height;
            int uvSize = uvStride * height / 2;

            IntPtr yBuf = Marshal.AllocHGlobal(ySize);
            IntPtr uBuf = Marshal.AllocHGlobal(uvSize);
            IntPtr vBuf = Marshal.AllocHGlobal(uvSize);

            dec.CopyVideoFrame(yBuf, uBuf, vBuf, yStride, uvStride);

            return new VideoFrame(0, yBuf, uBuf, vBuf, yStride, uvStride);
        }

        public void Dispose()
        {
            if (YPlane != IntPtr.Zero) Marshal.FreeHGlobal(YPlane);
            if (UPlane != IntPtr.Zero) Marshal.FreeHGlobal(UPlane);
            if (VPlane != IntPtr.Zero) Marshal.FreeHGlobal(VPlane);
        }
    }
}
