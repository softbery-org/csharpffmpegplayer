using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SDL2;
using Subtitles;

namespace CSharpFFmpeg;

public sealed class Player : IDisposable
{
    private readonly FFmpegDecoder _decoder = new();
    private SDLRenderer _renderer = new();
    private Thread? _decodeThread;

    private readonly object _audioLock = new();
    private readonly Queue<byte[]> _audioQueue = new();
    private const int MaxAudioQueueBytes = 384_000; // ~1s at 48kHz stereo s16
    private byte[]? _audioPartial;
    private int _audioPartialOffset;

    private readonly object _videoLock = new();
    private readonly Queue<VideoFrame> _videoQueue = new();
    private const int MaxVideoQueue = 5;

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
    private volatile bool _stopRequested;
    private bool _forceRedraw;
    private VideoFrame? _lastFrame;
    private bool _stopped;
    private bool _trackEnded;

    // Thumbnail preview
    private long   _thumbRequestBits = unchecked((long)0xFFF8000000000000L); // NaN = no request
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

    public void Play(string filePath)
    {
        _videoPath = filePath;
        _decoder.Open(filePath);

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

        // Restore session settings
        _renderer.Volume = RestoreVolume;
        _renderer.PlaylistPanelVisible = RestorePlaylistVisible;
        if (RestoreWinW > 0)
            _renderer.SetWindowGeometry(RestoreWinX, RestoreWinY, RestoreWinW, RestoreWinH);

        _decodeRunning = true;
        _trackEof = false;
        _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "Decode" };
        _decodeThread.Start();

        if (StartPositionSec > 1.0)
        {
            Console.Error.WriteLine($"[Player] Restoring position {StartPositionSec:F1}s");
            Thread.Sleep(300); // let decoder fill buffers first
            RequestSeek(StartPositionSec);
            StartPositionSec = 0;
        }

        EventLoop();
    }

    private void EventLoop()
    {
        while (_running)
        {
            while (SDL.SDL_PollEvent(out var ev) != 0)
            {
                switch (ev.type)
                {
                    case SDL.SDL_EventType.SDL_QUIT:
                        SaveSession();
                        _running = false;
                        break;
                    case SDL.SDL_EventType.SDL_KEYDOWN:
                        ShowUI();
                        switch (ev.key.keysym.sym)
                        {
                            case SDL.SDL_Keycode.SDLK_ESCAPE:
                                if (_renderer.AboutVisible)
                                {
                                    _renderer.AboutVisible = false;
                                    _forceRedraw = true;
                                }
                                else if (_fullscreen)
                                    ToggleFullscreen();
                                else
                                    _running = false;
                                break;
                            case SDL.SDL_Keycode.SDLK_q:
                                SaveSession();
                                _running = false;
                                break;
                            case SDL.SDL_Keycode.SDLK_SPACE:
                                _paused = !_paused;
                                if (_paused) _clock.Stop();
                                else _clock.Start();
                                _forceRedraw = true;
                                break;
                            case SDL.SDL_Keycode.SDLK_LEFT:
                                RequestSeek(Math.Max(0, GetMasterClock() - 10));
                                break;
                            case SDL.SDL_Keycode.SDLK_RIGHT:
                                RequestSeek(Math.Min(_duration, GetMasterClock() + 10));
                                break;
                            case SDL.SDL_Keycode.SDLK_n:
                                PlayNext();
                                break;
                            case SDL.SDL_Keycode.SDLK_p:
                                PlayPrev();
                                break;
                            case SDL.SDL_Keycode.SDLK_TAB:
                                _renderer.PlaylistPanelVisible = !_renderer.PlaylistPanelVisible;
                                if (_renderer.PlaylistPanelVisible && Playlist != null)
                                    _renderer.PlaylistSelectedIndex = Playlist.CurrentIndex;
                                else
                                    _renderer.PlaylistSelectedIndex = -1;
                                _forceRedraw = true;
                                break;
                            case SDL.SDL_Keycode.SDLK_UP:
                                if (_renderer.PlaylistPanelVisible && Playlist != null && Playlist.Count > 0)
                                {
                                    int sel = _renderer.PlaylistSelectedIndex < 0 ? Playlist.CurrentIndex : _renderer.PlaylistSelectedIndex;
                                    _renderer.PlaylistSelectedIndex = Math.Max(0, sel - 1);
                                    ScrollPlaylistToSelected();
                                    _forceRedraw = true;
                                }
                                break;
                            case SDL.SDL_Keycode.SDLK_DOWN:
                                if (_renderer.PlaylistPanelVisible && Playlist != null && Playlist.Count > 0)
                                {
                                    int sel = _renderer.PlaylistSelectedIndex < 0 ? Playlist.CurrentIndex : _renderer.PlaylistSelectedIndex;
                                    _renderer.PlaylistSelectedIndex = Math.Min(Playlist.Count - 1, sel + 1);
                                    ScrollPlaylistToSelected();
                                    _forceRedraw = true;
                                }
                                break;
                            case SDL.SDL_Keycode.SDLK_RETURN:
                            case SDL.SDL_Keycode.SDLK_KP_ENTER:
                                if (_renderer.PlaylistPanelVisible && Playlist != null)
                                {
                                    int idx = _renderer.PlaylistSelectedIndex;
                                    if (idx >= 0 && idx < Playlist.Count)
                                    {
                                        Playlist.MoveTo(idx);
                                        OpenNewFile(Playlist.Current);
                                    }
                                }
                                break;
                            case SDL.SDL_Keycode.SDLK_DELETE:
                            case SDL.SDL_Keycode.SDLK_BACKSPACE:
                                if (_renderer.PlaylistPanelVisible && Playlist != null)
                                {
                                    int sel = _renderer.PlaylistSelectedIndex;
                                    if (sel >= 0 && sel < Playlist.Count)
                                    {
                                        bool removingCurrent = sel == Playlist.CurrentIndex;
                                        Playlist.RemoveAt(sel);
                                        // Clamp selection to new bounds
                                        _renderer.PlaylistSelectedIndex = Playlist.Count == 0
                                            ? -1
                                            : Math.Clamp(sel, 0, Playlist.Count - 1);
                                        if (removingCurrent && Playlist.Count > 0)
                                            OpenNewFile(Playlist.Current);
                                        else if (removingCurrent)
                                        {
                                            _paused = true;
                                            _clock.Stop();
                                        }
                                        _forceRedraw = true;
                                    }
                                }
                                break;
                        }
                        break;
                    case SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN:
                        ShowUI();

                        // Right-click: context menu on playlist item
                        if (ev.button.button == 3)
                        {
                            if (_renderer.IsContextMenuVisible)
                            {
                                _renderer.HideContextMenu();
                                _forceRedraw = true;
                                break;
                            }
                            int itemIdx = _renderer.GetPlaylistItemIndexAt(ev.button.y);
                            if (itemIdx >= 0 && ev.button.x < 340 && _renderer.PlaylistPanelVisible)
                            {
                                _renderer.ShowContextMenu(ev.button.x, ev.button.y, itemIdx);
                                _forceRedraw = true;
                            }
                            break;
                        }

                        if (ev.button.button == 1)
                        {
                            // Context menu selection
                            if (_renderer.IsContextMenuVisible)
                            {
                                int targetItem = _renderer.ContextMenuTargetItem;
                                var action = _renderer.HitTestContextMenu(ev.button.x, ev.button.y);
                                _forceRedraw = true;
                                HandleContextMenuAction(action, targetItem);
                                break;
                            }

                            // Playlist panel clicks
                            var plHit = _renderer.HitTestPlaylistPanel(ev.button.x, ev.button.y);
                            if (plHit == SDLRenderer.PlaylistPanelHit.RepeatMode)
                            {
                                CycleRepeatMode();
                                _forceRedraw = true;
                                break;
                            }
                            if (plHit == SDLRenderer.PlaylistPanelHit.AddFile)
                            {
                                var files = OpenFileDialogMultiple();
                                ResyncClock();
                                if (files != null && Playlist != null)
                                {
                                    foreach (var f in files) Playlist.Add(f);
                                    _forceRedraw = true;
                                }
                                break;
                            }
                            if (plHit == SDLRenderer.PlaylistPanelHit.AddFolder)
                            {
                                string? folder = OpenFolderDialog();
                                ResyncClock();
                                if (folder != null && Playlist != null)
                                {
                                    Playlist.AddFolder(folder);
                                    _forceRedraw = true;
                                }
                                break;
                            }
                            if (plHit == SDLRenderer.PlaylistPanelHit.ClearPlaylist)
                            {
                                if (Playlist != null)
                                {
                                    Playlist.Clear();
                                    _renderer.PlaylistSelectedIndex = -1;
                                    _paused = true;
                                    _clock.Stop();
                                    _forceRedraw = true;
                                }
                                break;
                            }
                            if (plHit == SDLRenderer.PlaylistPanelHit.ItemClick)
                            {
                                int idx = _renderer.PlaylistPanelClickedItem;
                                if (Playlist != null && idx >= 0 && idx < Playlist.Count)
                                {
                                    // Start drag — commit on mouseup
                                    _plDragFromIndex = idx;
                                    _plDragging = true;
                                    _renderer.DragFromIndex = idx;
                                    _renderer.DragToIndex = idx;
                                    _forceRedraw = true;
                                }
                                break;
                            }

                            var barBtn = _renderer.HitTestBarButton(ev.button.x, ev.button.y);
                            if (barBtn == SDLRenderer.BarButton.PlaylistToggle)
                            {
                                _renderer.PlaylistPanelVisible = !_renderer.PlaylistPanelVisible;
                                if (_renderer.PlaylistPanelVisible && Playlist != null)
                                    _renderer.PlaylistSelectedIndex = Playlist.CurrentIndex;
                                _forceRedraw = true;
                                break;
                            }
                            if (barBtn == SDLRenderer.BarButton.RepeatMode)
                            {
                                CycleRepeatMode();
                                _forceRedraw = true;
                                break;
                            }
                            if (barBtn == SDLRenderer.BarButton.About)
                            {
                                _renderer.AboutVisible = !_renderer.AboutVisible;
                                _forceRedraw = true;
                                break;
                            }

                            var btn = _renderer.HitTestControl(ev.button.x, ev.button.y);
                            if (btn != SDLRenderer.ControlButton.None)
                            {
                                HandleControlButton(btn);
                                break;
                            }
                            float volHit = _renderer.HitTestVolumeBar(ev.button.x, ev.button.y);
                            if (volHit >= 0f)
                            {
                                _renderer.Volume = volHit;
                                _volDragging = true;
                                _forceRedraw = true;
                                break;
                            }
                            if (IsOnProgressBar(ev.button.x, ev.button.y))
                            {
                                _mouseDragging = true;
                                HandleSeekByMouse(ev.button.x);
                            }
                            else
                            {
                                long now = Environment.TickCount64;
                                if (now - _lastClickTicks < DoubleClickMs)
                                {
                                    ToggleFullscreen();
                                    _lastClickTicks = 0;
                                }
                                else
                                {
                                    _lastClickTicks = now;
                                }
                            }
                        }
                        break;
                    case SDL.SDL_EventType.SDL_MOUSEBUTTONUP:
                        if (ev.button.button == 1)
                        {
                            if (_volDragging)
                            {
                                _volDragging = false;
                                break;
                            }
                            if (_plDragging)
                            {
                                int dropIdx = _renderer.GetPlaylistDropIndex(ev.button.y);
                                // Clamp drop to valid insert positions
                                int toIdx = dropIdx > _plDragFromIndex ? dropIdx - 1 : dropIdx;
                                if (toIdx != _plDragFromIndex && Playlist != null)
                                {
                                    Console.Error.WriteLine($"[Player] Playlist move {_plDragFromIndex} -> {toIdx}");
                                    Playlist.Move(_plDragFromIndex, toIdx);
                                }
                                else if (toIdx == _plDragFromIndex && Playlist != null)
                                {
                                    // First click → select; second click on same item → play
                                    if (_renderer.PlaylistSelectedIndex == _plDragFromIndex)
                                    {
                                        Playlist.MoveTo(_plDragFromIndex);
                                        OpenNewFile(Playlist.Current);
                                    }
                                    else
                                    {
                                        _renderer.PlaylistSelectedIndex = _plDragFromIndex;
                                    }
                                }
                                _plDragging = false;
                                _plDragFromIndex = -1;
                                _renderer.DragFromIndex = -1;
                                _renderer.DragToIndex = -1;
                                _forceRedraw = true;
                            }
                            else
                            {
                                _mouseDragging = false;
                            }
                        }
                        break;
                    case SDL.SDL_EventType.SDL_MOUSEMOTION:
                        _mouseX = ev.motion.x;
                        _mouseY = ev.motion.y;
                        if (_renderer.PlaylistPanelVisible && ev.motion.x < SDLRenderer.PlaylistPanelWidth)
                        {
                            int hov = _renderer.GetPlaylistItemIndexAt(ev.motion.y);
                            if (hov != _renderer.PlaylistHoverIndex)
                            {
                                _renderer.PlaylistHoverIndex = hov;
                                _forceRedraw = true;
                            }
                        }
                        else if (_renderer.PlaylistHoverIndex >= 0)
                        {
                            _renderer.PlaylistHoverIndex = -1;
                            _forceRedraw = true;
                        }
                        if (_volDragging)
                        {
                            float v = _renderer.SampleVolumeBarX(ev.motion.x);
                            if (v >= 0f) _renderer.Volume = v;
                            _forceRedraw = true;
                        }
                        else if (_plDragging && ev.motion.x < 340)
                        {
                            _renderer.DragToIndex = _renderer.GetPlaylistDropIndex(ev.motion.y);
                            _forceRedraw = true;
                        }
                        else if (_mouseDragging)
                        {
                            HandleSeekByMouse(ev.motion.x);
                        }
                        UpdateHoveredButton(ev.motion.x, ev.motion.y);
                        UpdateProgressBarHover(ev.motion.x, ev.motion.y);
                        ShowUI();
                        break;
                    case SDL.SDL_EventType.SDL_MOUSEWHEEL:
                        if (_renderer.PlaylistPanelVisible && _mouseX < SDLRenderer.PlaylistPanelWidth)
                        {
                            _renderer.ScrollPlaylist(-ev.wheel.y);
                        }
                        else
                        {
                            float delta = ev.wheel.y * 0.05f;
                            _renderer.Volume = Math.Clamp(_renderer.Volume + delta, 0f, 1.5f);
                        }
                        _forceRedraw = true;
                        break;
                }
            }

            _renderer.IsPlaying = !_paused;
            _renderer.SetProgress(GetMasterClock());
            UpdatePlaylistPanel();

            if (!_paused)
                TryRenderNextFrame();
            else if (_forceRedraw)
                RedrawLastFrame();

            UpdateSubtitle();

            if (_pendingOpenFile != null)
            {
                string file = _pendingOpenFile;
                _pendingOpenFile = null;
                OpenNewFile(file);
            }

            if (_stopRequested)
            {
                _stopRequested = false;
                _paused = true;
                _clock.Stop();
                ClearQueues();
                _renderer.SetProgress(0);
            }

            // Auto-advance when track ends:
            // Either clock reaches end, OR decoder hit EOF and audio queue fully drained
            // _clockStarted guard prevents instant trigger on startup seek near EOF
            // _pendingOpenFile guard prevents double-open when user opened a file in same frame
            bool eofDrained = _trackEof && _clockStarted && _audioQueue.Count == 0 && _pendingOpenFile == null;
            bool clockEnd   = _clockStarted && _duration > 1.0 && GetMasterClock() >= _duration - 0.15;
            if (!_trackEnded && (clockEnd || eofDrained))
            {
                _trackEnded = true;
                if (Playlist != null)
                {
                    bool advanced = Playlist.AdvanceToNext();
                    if (advanced)
                    {
                        OpenNewFile(Playlist.Current);
                    }
                    else
                    {
                        // End of playlist — pause and stop clock
                        _paused = true;
                        _clock.Stop();
                        Console.Error.WriteLine("[Player] Playlist ended");
                    }
                }
                else
                {
                    _paused = true;
                    _clock.Stop();
                }
            }

            // Sync repeat mode to renderer
            if (Playlist != null)
                _renderer.PlaylistRepeatMode = Playlist.RepeatMode;

            if (!_uiHidden && _hoveredButton == -1 && Environment.TickCount64 - _lastMouseMotionTicks > AutoHideMs)
                HideUI();

            int sleepMs = _targetFps > 0 ? Math.Max(1, 1000 / _targetFps - 2) : 5;
            Thread.Sleep(sleepMs);
        }
    }

    private void TryRenderNextFrame()
    {
        if (_decoder.VideoStreamIndex < 0) return;

        VideoFrame? frame = null;
        lock (_videoLock)
        {
            if (_videoQueue.Count > 0)
                frame = _videoQueue.Dequeue();
        }

        if (frame == null) return;

        double delay = frame.Value.Pts - GetMasterClock();
        if (delay > 0.01)
        {
            // Too early — put it back and wait
            lock (_videoLock) _videoQueue.Enqueue(frame.Value);
            return;
        }
        else if (delay < -0.1)
        {
            // Too late — drop this frame, try next
            frame.Value.Dispose();
            return;
        }

        _renderer.UpdateVideoFrame(frame.Value.YPlane, frame.Value.UPlane, frame.Value.VPlane,
            frame.Value.YStride, frame.Value.UVStride);
        _lastFrame?.Dispose();
        _lastFrame = frame;
    }

    private void RedrawLastFrame()
    {
        _forceRedraw = false;
        if (_lastFrame == null || _decoder.VideoStreamIndex < 0)
        {
            _renderer.RenderUI();
            return;
        }
        _renderer.UpdateVideoFrame(_lastFrame.Value.YPlane, _lastFrame.Value.UPlane, _lastFrame.Value.VPlane,
            _lastFrame.Value.YStride, _lastFrame.Value.UVStride);
    }

    private void DecodeLoop()
    {
        while (_decodeRunning)
        {
            if (_paused) { Thread.Sleep(20); continue; }

            if (_seekRequested)
            {
                _seekRequested = false;
                ClearQueues();
                _decoder.Seek(_seekTarget);
                _clockStarted = false;
                continue;
            }

            // Backpressure: don't queue too many video frames
            lock (_videoLock)
            {
                if (_videoQueue.Count >= MaxVideoQueue)
                {
                    Monitor.Wait(_videoLock, 10);
                    continue;
                }
            }

            if (!_decoder.ReadPacket(out int streamIdx))
            {
                _trackEof = true;
                _decodeRunning = false;
                break;
            }

            _decoder.SendPacket();
            _decoder.UnrefPacket();

            if (streamIdx == _decoder.VideoStreamIndex)
            {
                while (_decoder.ReceiveVideoFrame())
                {
                    double pts = _decoder.GetVideoFramePts();
                    var vf = VideoFrame.FromDecoder(_decoder, _videoWidth, _videoHeight);
                    var vfWithPts = new VideoFrame(pts, vf.YPlane, vf.UPlane, vf.VPlane, vf.YStride, vf.UVStride);
                    lock (_videoLock)
                    {
                        _videoQueue.Enqueue(vfWithPts);
                        Monitor.Pulse(_videoLock);
                    }
                    _decoder.UnrefFrame();
                }
            }
            else if (streamIdx == _decoder.AudioStreamIndex)
            {
                while (_decoder.ReceiveAudioFrame())
                {
                    _audioPts = _decoder.GetAudioFramePts();
                    if (!_clockStarted)
                    {
                        _clockBasePts = _audioPts;
                        _clock.Restart();
                        _clockStarted = true;
                    }
                    var pcm = _decoder.CopyAudioFrame();
                    if (pcm.Length > 0)
                    {
                        lock (_audioLock)
                        {
                            int totalBytes = 0;
                            foreach (var c in _audioQueue) totalBytes += c.Length;
                            while (totalBytes + pcm.Length > MaxAudioQueueBytes && _audioQueue.Count > 0)
                            {
                                var old = _audioQueue.Dequeue();
                                totalBytes -= old.Length;
                            }
                            _audioQueue.Enqueue(pcm);
                        }
                    }
                    _decoder.UnrefFrame();
                }
            }
        }
    }

    private double GetMasterClock()
    {
        if (!_clockStarted) return 0;
        return _clockBasePts + _clock.Elapsed.TotalSeconds;
    }

    private void HandleControlButton(SDLRenderer.ControlButton btn)
    {
        switch (btn)
        {
            case SDLRenderer.ControlButton.PlayPause:
                if (_stopped)
                {
                    _stopped = false;
                    _paused = false;
                    RequestSeek(0);
                    _clock.Reset();
                    _clock.Start();
                    _clockStarted = false;
                }
                else
                {
                    _paused = !_paused;
                    if (_paused) _clock.Stop();
                    else _clock.Start();
                }
                _forceRedraw = true;
                break;
            case SDLRenderer.ControlButton.Stop:
                _stopRequested = true;
                _stopped = true;
                break;
            case SDLRenderer.ControlButton.OpenFile:
                string? file = OpenFileDialog();
                ResyncClock();
                if (file != null)
                    _pendingOpenFile = file;
                break;
        }
    }

    private void UpdateHoveredButton(int x, int y)
    {
        var btn = _renderer.HitTestControl(x, y);
        _hoveredButton = btn == SDLRenderer.ControlButton.None ? -1 : (int)btn - 1;
        _renderer.SetHoveredButton(_hoveredButton);
        if (_hoveredButton != -1)
            _lastMouseMotionTicks = Environment.TickCount64;
    }

    private void OpenNewFile(string filePath)
    {
        Console.Error.WriteLine($"[Player] OpenNewFile: {filePath}");

        // Stop decode thread safely — do NOT set _running=false (that exits EventLoop)
        _decodeRunning = false;
        _renderer.StopAudio();
        // Wake DecodeLoop if it's waiting in backpressure Monitor.Wait
        lock (_videoLock) { Monitor.PulseAll(_videoLock); }
        lock (_audioLock) { Monitor.PulseAll(_audioLock); }
        if (_decodeThread != null && _decodeThread.IsAlive)
        {
            bool joined = _decodeThread.Join(3000);
            Console.Error.WriteLine($"[Player] DecodeThread joined={joined}");
        }

        ClearQueues();
        _lastFrame?.Dispose();
        _lastFrame = null;

        _decoder.Dispose();
        Console.Error.WriteLine($"[Player] Decoder disposed, opening: {filePath}");

        _running = true;
        _decodeRunning = true;
        _trackEof = false;
        _paused = false;
        _clockStarted = false;
        _stopped = false;
        _trackEnded = false;
        _clock.Reset();

        try
        {
            _decoder.UseHwAccel = _useHwAccel;
            _decoder.Open(filePath);
            Console.Error.WriteLine($"[Player] Opened: video={_decoder.VideoStreamIndex} audio={_decoder.AudioStreamIndex} dur={_decoder.DurationSec:F1}s");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Player] Open failed: {ex.Message}");
            return;
        }

        _videoWidth  = _decoder.VideoWidth;
        _videoHeight = _decoder.VideoHeight;
        _duration    = _decoder.DurationSec;
        _videoPath   = filePath;

        // Validate decoded stream parameters — skip broken files gracefully
        bool videoOk = _decoder.VideoStreamIndex < 0 || (_videoWidth > 0 && _videoHeight > 0);
        bool audioOk = _decoder.AudioStreamIndex < 0 || _decoder.SampleRate > 0;
        if (!videoOk || !audioOk)
        {
            string reason = !videoOk ? $"nieprawidłowa rozdzielczość ({_videoWidth}x{_videoHeight})"
                                     : $"nieprawidłowy format audio (rate={_decoder.SampleRate})";
            Console.Error.WriteLine($"[Player] Skipping broken file: {reason} — {filePath}");
            _renderer.ShowError($"Błąd odtwarzania:\n{Path.GetFileName(filePath)}\n{reason}");
            _decodeRunning = false;
            // Auto-skip to next after 3 s (flag _trackEnded so EventLoop advances)
            _trackEnded = false;
            _trackEof   = true;
            _clockStarted = true; // allow eofDrained to fire
            return;
        }

        string title = Path.GetFileNameWithoutExtension(filePath);
        if (_decoder.VideoStreamIndex >= 0)
            _renderer.ReinitForNewFile(_videoWidth, _videoHeight, _duration, title);
        else
            _renderer.ReinitForNewFile(0, 0, _duration, title);

        TryLoadSubtitles(filePath);

        if (_decoder.AudioStreamIndex >= 0)
        {
            Console.Error.WriteLine($"[Player] InitAudio rate={_decoder.SampleRate}");
            _renderer.InitAudio(_decoder.SampleRate, 2, OnAudioCallback);
        }

        _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "Decode" };
        _decodeThread.Start();
        Console.Error.WriteLine($"[Player] DecodeThread started");
    }

    private void TryLoadSubtitles(string videoPath)
    {
        _subtitles = null;
        _renderer.SubtitleText = null;

        // If subtitle path was provided via CLI, use it directly
        if (!string.IsNullOrEmpty(SubtitlePath))
        {
            if (File.Exists(SubtitlePath))
            {
                try
                {
                    _subtitles = new SubtitleManager(SubtitlePath);
                    Console.Error.WriteLine($"[Subtitles] Loaded: {SubtitlePath} ({_subtitles.Count} entries)");
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Subtitles] Failed to load {SubtitlePath}: {ex.Message}");
                }
            }
            else
            {
                Console.Error.WriteLine($"[Subtitles] File not found: {SubtitlePath}");
            }
        }

        // Auto-detect subtitle file next to video
        string baseName = System.IO.Path.GetFileNameWithoutExtension(videoPath);
        string dir = System.IO.Path.GetDirectoryName(videoPath) ?? "";
        string[] subExts = { ".srt", ".sub", ".txt" };

        foreach (var ext in subExts)
        {
            string subPath = System.IO.Path.Combine(dir, baseName + ext);
            if (File.Exists(subPath))
            {
                try
                {
                    _subtitles = new SubtitleManager(subPath);
                    Console.Error.WriteLine($"[Subtitles] Loaded: {subPath} ({_subtitles.Count} entries)");
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Subtitles] Failed to load {subPath}: {ex.Message}");
                }
            }
        }
    }

    private void ScrollPlaylistToSelected()
    {
        _renderer.ScrollPlaylistToIndex(_renderer.PlaylistSelectedIndex);
    }

    // Snap master clock to current audio position — call after returning from a blocking dialog
    // so the event loop doesn't try to catch up rendered frames
    private void ResyncClock()
    {
        if (!_clockStarted || _paused) return;
        _clockBasePts = _audioPts;
        _clock.Restart();
    }

    private void HandleContextMenuAction(SDLRenderer.ContextMenuAction action, int itemIndex)
    {
        if (Playlist == null || itemIndex < 0 || itemIndex >= Playlist.Count) return;
        switch (action)
        {
            case SDLRenderer.ContextMenuAction.Play:
                Playlist.MoveTo(itemIndex);
                OpenNewFile(Playlist.Current);
                break;
            case SDLRenderer.ContextMenuAction.PlayNext:
                // Insert item right after current playing index
                if (itemIndex != Playlist.CurrentIndex)
                {
                    int insertAt = Playlist.CurrentIndex + 1;
                    if (insertAt > itemIndex) insertAt--;  // account for removal shift
                    Playlist.Move(itemIndex, Math.Clamp(insertAt, 0, Playlist.Count - 1));
                }
                break;
            case SDLRenderer.ContextMenuAction.RemoveFromPlaylist:
                Playlist.RemoveAt(itemIndex);
                break;
        }
    }

    private void CycleRepeatMode()
    {
        if (Playlist == null) return;
        Playlist.RepeatMode = Playlist.RepeatMode switch
        {
            RepeatMode.Once    => RepeatMode.All,
            RepeatMode.All     => RepeatMode.One,
            RepeatMode.One     => RepeatMode.Shuffle,
            RepeatMode.Shuffle => RepeatMode.Once,
            _                  => RepeatMode.Once,
        };
    }

    private void PlayNext()
    {
        if (Playlist == null || !Playlist.HasNext) return;
        Playlist.MoveNext();
        OpenNewFile(Playlist.Current);
    }

    private void PlayPrev()
    {
        if (Playlist == null || !Playlist.HasPrev) return;
        Playlist.MovePrev();
        OpenNewFile(Playlist.Current);
    }

    private void UpdateSubtitle()
    {
        if (_subtitles == null || _subtitles.Count == 0)
        {
            _renderer.SubtitleText = null;
            return;
        }

        double clock = GetMasterClock();
        var sub = _subtitles.GetSubtitleAtTime(TimeSpan.FromSeconds(clock));
        _renderer.SubtitleText = sub != null ? string.Join("\n", sub.TextLines) : null;
    }

    private void UpdatePlaylistPanel()
    {
        if (Playlist == null || Playlist.Count == 0) return;
        var files = Playlist.Files;
        var names = new string[files.Count];
        var durations = new double[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            names[i] = System.IO.Path.GetFileNameWithoutExtension(files[i]);
            durations[i] = i == Playlist.CurrentIndex ? _duration : 0;
        }
        _renderer.SetPlaylistData(names, durations, Playlist.CurrentIndex, GetMasterClock());
    }

    private static string? OpenFileDialog()
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "zenity", UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("--file-selection");
            psi.ArgumentList.Add("--title=Open Media");
            psi.ArgumentList.Add("--file-filter=All files|*");
            psi.ArgumentList.Add("--file-filter=Video|*.mp4 *.mkv *.avi *.mov *.webm *.flv *.wmv *.mpg *.mpeg *.m4v *.ts *.m2ts *.m3u8 *.vob *.ogv *.3gp *.rm *.rmvb *.asf *.f4v *.dv");
            psi.ArgumentList.Add("--file-filter=Audio|*.mp3 *.aac *.flac *.wav *.ogg *.opus *.m4a *.wma *.ac3 *.dts *.amr *.aiff *.alac");
            var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                return output;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"File dialog error: {ex.Message}");
        }
        return null;
    }

    private static List<string>? OpenFileDialogMultiple()
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "zenity", UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("--file-selection");
            psi.ArgumentList.Add("--multiple");
            psi.ArgumentList.Add("--separator=|");
            psi.ArgumentList.Add("--title=Add Media Files");
            psi.ArgumentList.Add("--file-filter=All files|*");
            psi.ArgumentList.Add("--file-filter=Video|*.mp4 *.mkv *.avi *.mov *.webm *.flv *.wmv *.mpg *.mpeg *.m4v *.ts *.m2ts *.m3u8 *.vob *.ogv *.3gp *.rm *.rmvb *.asf *.f4v *.dv");
            psi.ArgumentList.Add("--file-filter=Audio|*.mp3 *.aac *.flac *.wav *.ogg *.opus *.m4a *.wma *.ac3 *.dts *.amr *.aiff *.alac");
            var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                return output.Split('|').Where(f => !string.IsNullOrEmpty(f)).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"File dialog error: {ex.Message}");
        }
        return null;
    }

    private static string? OpenFolderDialog()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "zenity",
                Arguments = "--file-selection --directory --title='Add Media Folder'",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                return output;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Folder dialog error: {ex.Message}");
        }
        return null;
    }

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        _renderer.SetFullscreen(_fullscreen);
    }

    private void ShowUI()
    {
        _lastMouseMotionTicks = Environment.TickCount64;
        if (_uiHidden)
        {
            _uiHidden = false;
            _renderer.ProgressBarVisible = true;
            _renderer.ControlsVisible = true;
            SDL.SDL_ShowCursor(SDL.SDL_ENABLE);
        }
    }

    private void HideUI()
    {
        _uiHidden = true;
        _renderer.ProgressBarVisible = false;
        _renderer.ControlsVisible = false;
        SDL.SDL_ShowCursor(SDL.SDL_DISABLE);
    }

    private void EnsureThumbWorkerRunning()
    {
        if (_thumbThread != null && _thumbThread.IsAlive) return;
        _thumbThread = new Thread(ThumbnailWorkerLoop) { IsBackground = true, Name = "Thumb" };
        _thumbThread.Start();
    }

    private void UpdateProgressBarHover(int x, int y)
    {
        if (_duration <= 0 || _videoPath == null) { _renderer.ProgressHoverTime = -1; return; }
        int barY = _renderer.GetProgressBarY();
        int margin = 20;
        _renderer.GetWindowGeometry(out _, out _, out int winW, out _);
        int barW = winW - margin * 2;
        bool onTrack = y >= barY && y <= barY + 70 && x >= margin && x <= margin + barW;
        if (!onTrack) { _renderer.ProgressHoverTime = -1; return; }

        double hoverTime = Math.Clamp((x - margin) / (double)barW * _duration, 0, _duration);
        _renderer.ProgressHoverTime = hoverTime;
        _forceRedraw = true;

        // Post latest request to worker — always overwrite, worker picks up latest
        Interlocked.Exchange(ref _thumbRequestBits, BitConverter.DoubleToInt64Bits(hoverTime));
        EnsureThumbWorkerRunning();
        _thumbSignal.Set();
    }

    private void ThumbnailWorkerLoop()
    {
        while (_running)
        {
            _thumbSignal.Wait();
            _thumbSignal.Reset();

            // Debounce: wait briefly, then take the latest pending request
            Thread.Sleep(80);
            double requestedTime = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _thumbRequestBits));
            if (double.IsNaN(requestedTime) || !_running) continue;

            string? path = _videoPath;
            if (path == null || _duration <= 0) continue;

            try
            {
                using var dec = new FFmpegDecoder();
                dec.Open(path);
                if (dec.VideoStreamIndex < 0) continue;

                // Always use the very latest request after decode starts
                double decodeTime = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _thumbRequestBits));
                if (double.IsNaN(decodeTime)) continue;

                var rgb = dec.DecodeThumbnailRgba(decodeTime, SDLRenderer.ThumbW, SDLRenderer.ThumbH);
                if (rgb != null && _running)
                {
                    _renderer.SetThumbnail(rgb, decodeTime);
                    _forceRedraw = true;
                }
            }
            catch { }
        }
    }

    private bool IsOnProgressBar(int x, int y)
    {
        int barY = _renderer.GetProgressBarY();
        return y >= barY - 5 && y <= barY + 65 && x >= 0;
    }

    private void HandleSeekByMouse(int mouseX)
    {
        int winW = _renderer.GetWindowWidth();
        int margin = 20;
        double frac = Math.Clamp((double)(mouseX - margin) / (winW - margin * 2), 0, 1);
        double target = frac * _duration;
        _renderer.SetProgress(target);
        RequestSeek(target);
    }

    private void RequestSeek(double target)
    {
        _seekTarget = target;
        _seekRequested = true;
    }

    private void ClearQueues()
    {
        lock (_videoLock)
        {
            while (_videoQueue.Count > 0)
            {
                var f = _videoQueue.Dequeue();
                f.Dispose();
            }
            Monitor.Pulse(_videoLock);
        }
        lock (_audioLock)
        {
            _audioQueue.Clear();
        }
        _audioPartial = null;
        _audioPartialOffset = 0;
    }

    private void OnAudioCallback(byte[] buf)
    {
        int offset = 0;
        while (offset < buf.Length)
        {
            // First, consume any partial chunk from previous callback
            if (_audioPartial != null)
            {
                int copy = Math.Min(_audioPartial.Length - _audioPartialOffset, buf.Length - offset);
                Array.Copy(_audioPartial, _audioPartialOffset, buf, offset, copy);
                offset += copy;
                _audioPartialOffset += copy;
                if (_audioPartialOffset >= _audioPartial.Length)
                {
                    _audioPartial = null;
                    _audioPartialOffset = 0;
                }
                continue;
            }

            byte[]? chunk = null;
            lock (_audioLock)
            {
                if (_audioQueue.Count > 0)
                    chunk = _audioQueue.Dequeue();
            }
            if (chunk == null)
            {
                Array.Clear(buf, offset, buf.Length - offset);
                break;
            }

            int copyLen = Math.Min(chunk.Length, buf.Length - offset);
            Array.Copy(chunk, 0, buf, offset, copyLen);
            offset += copyLen;

            if (copyLen < chunk.Length)
            {
                // Save remainder in order — it will be consumed first next time
                _audioPartial = chunk;
                _audioPartialOffset = copyLen;
            }
        }
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
