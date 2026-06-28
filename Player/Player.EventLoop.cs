using SDL2;

namespace CSharpFFmpeg;

public sealed partial class Player
{
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
                        HandleKeyDown(ev.key.keysym.sym);
                        break;
                    case SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN:
                        ShowUI();
                        HandleMouseButtonDown(ev);
                        break;
                    case SDL.SDL_EventType.SDL_MOUSEBUTTONUP:
                        HandleMouseButtonUp(ev);
                        break;
                    case SDL.SDL_EventType.SDL_MOUSEMOTION:
                        HandleMouseMotion(ev);
                        break;
                    case SDL.SDL_EventType.SDL_MOUSEWHEEL:
                        HandleMouseWheel(ev);
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

            if (_pendingPrevNext != 0 && Playlist != null)
            {
                int dir = _pendingPrevNext;
                _pendingPrevNext = 0;
                bool advanced = dir > 0 ? Playlist.AdvanceToNext() : Playlist.AdvanceToPrev();
                if (advanced)
                    OpenNewFile(Playlist.Current, Playlist.CurrentEntry);
            }

            if (_stopRequested)
            {
                _stopRequested = false;
                _paused = true;
                _clock.Stop();
                ClearQueues();
                _renderer.SetProgress(0);
            }

            long trackAge = Environment.TickCount64 - _trackOpenTicks;
            long sinceReopen = Environment.TickCount64 - _lastReopenTicks;
            bool eofDrained = _trackEof && _clockStarted && _audioQueue.Count == 0 && _pendingOpenFile == null
                              && trackAge > 3000 && sinceReopen > 5000;
            bool clockEnd   = _clockStarted && _duration > 1.0 && GetMasterClock() >= _duration - 0.15;
            if (!_trackEnded && (clockEnd || eofDrained))
            {
                _trackEnded = true;
                if (Playlist != null && Playlist.Count > 0)
                {
                    bool advanced = Playlist.AdvanceToNext();
                    if (advanced)
                    {
                        OpenNewFile(Playlist.Current, Playlist.CurrentEntry);
                    }
                    else
                    {
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

            if (Playlist != null)
                _renderer.PlaylistRepeatMode = Playlist.RepeatMode;

            if (!_uiHidden && _hoveredButton == -1 && Environment.TickCount64 - _lastMouseMotionTicks > AutoHideMs)
                HideUI();

            int sleepMs = _targetFps > 0 ? Math.Max(1, 1000 / _targetFps - 2) : 2;
            Thread.Sleep(sleepMs);
        }
    }

    private void HandleKeyDown(SDL.SDL_Keycode key)
    {
        switch (key)
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
                        OpenNewFile(Playlist.Current, Playlist.CurrentEntry);
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
                        _renderer.PlaylistSelectedIndex = Playlist.Count == 0
                            ? -1
                            : Math.Clamp(sel, 0, Playlist.Count - 1);
                        if (removingCurrent && Playlist.Count > 0)
                            OpenNewFile(Playlist.Current, Playlist.CurrentEntry);
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
    }

    private void HandleMouseButtonDown(SDL.SDL_Event ev)
    {
        if (ev.button.button == 3)
        {
            if (_renderer.IsContextMenuVisible)
            {
                _renderer.HideContextMenu();
                _forceRedraw = true;
                return;
            }
            int itemIdx = _renderer.GetPlaylistItemIndexAt(ev.button.y);
            if (itemIdx >= 0 && ev.button.x < SDLRenderer.PlaylistPanelWidth && _renderer.PlaylistPanelVisible)
            {
                _renderer.ShowContextMenu(ev.button.x, ev.button.y, itemIdx);
                _forceRedraw = true;
            }
            return;
        }

        if (ev.button.button != 1) return;

        // Window resize — check edges first
        if (!_uiHidden)
        {
            var resizeEdge = _renderer.HitTestResize(ev.button.x, ev.button.y);
            if (resizeEdge != SDLRenderer.ResizeEdge.None)
            {
                _windowResizing = true;
                _resizeEdge = (int)resizeEdge;
                _renderer.GetWindowPosition(out _resizeStartWinX, out _resizeStartWinY);
                _resizeStartW = _renderer.GetWindowWidth();
                _resizeStartH = _renderer.GetWindowHeight();
                return;
            }
        }

        var titleHit = _renderer.HitTestTitleBar(ev.button.x, ev.button.y);
        if (titleHit == SDLRenderer.TitleBarButton.Close)
        {
            SaveSession();
            _running = false;
            return;
        }
        if (titleHit == SDLRenderer.TitleBarButton.Minimize)
        {
            _renderer.MinimizeWindow();
            return;
        }

        // Window dragging — click on empty title bar area
        if (_renderer.IsInTitleBarDragArea(ev.button.x, ev.button.y))
        {
            _windowDragging = true;
            _renderer.GetWindowPosition(out int winX, out int winY);
            _windowDragOffsetX = ev.button.x - winX;
            _windowDragOffsetY = ev.button.y - winY;
            return;
        }

        if (_renderer.IsContextMenuVisible)
        {
            int targetItem = _renderer.ContextMenuTargetItem;
            var action = _renderer.HitTestContextMenu(ev.button.x, ev.button.y);
            _forceRedraw = true;
            HandleContextMenuAction(action, targetItem);
            return;
        }

        var plHit = _renderer.HitTestPlaylistPanel(ev.button.x, ev.button.y);
        if (HandlePlaylistPanelHit(plHit)) return;

        var barBtn = _renderer.HitTestBarButton(ev.button.x, ev.button.y);
        if (barBtn == SDLRenderer.BarButton.PlaylistToggle)
        {
            _renderer.PlaylistPanelVisible = !_renderer.PlaylistPanelVisible;
            if (_renderer.PlaylistPanelVisible && Playlist != null)
                _renderer.PlaylistSelectedIndex = Playlist.CurrentIndex;
            _forceRedraw = true;
            return;
        }
        if (barBtn == SDLRenderer.BarButton.RepeatMode)
        {
            CycleRepeatMode();
            _forceRedraw = true;
            return;
        }
        if (barBtn == SDLRenderer.BarButton.About)
        {
            _renderer.AboutVisible = !_renderer.AboutVisible;
            _forceRedraw = true;
            return;
        }

        var btn = _renderer.HitTestControl(ev.button.x, ev.button.y);
        if (btn != SDLRenderer.ControlButton.None)
        {
            HandleControlButton(btn);
            return;
        }
        float volHit = _renderer.HitTestVolumeBar(ev.button.x, ev.button.y);
        if (volHit >= 0f)
        {
            _renderer.Volume = volHit;
            _volDragging = true;
            _forceRedraw = true;
            return;
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

    private void HandleMouseButtonUp(SDL.SDL_Event ev)
    {
        if (ev.button.button != 1) return;
        if (_windowResizing)
        {
            _windowResizing = false;
            _resizeEdge = 0;
            return;
        }
        if (_windowDragging)
        {
            _windowDragging = false;
            return;
        }
        if (_volDragging)
        {
            _volDragging = false;
            return;
        }
        if (_plDragging)
        {
            int dropIdx = _renderer.GetPlaylistDropIndex(ev.button.y);
            int toIdx = dropIdx > _plDragFromIndex ? dropIdx - 1 : dropIdx;
            if (toIdx != _plDragFromIndex && Playlist != null)
            {
                Console.Error.WriteLine($"[Player] Playlist move {_plDragFromIndex} -> {toIdx}");
                Playlist.Move(_plDragFromIndex, toIdx);
            }
            else if (toIdx == _plDragFromIndex && Playlist != null)
            {
                if (_renderer.PlaylistSelectedIndex == _plDragFromIndex)
                {
                    Playlist.MoveTo(_plDragFromIndex);
                    OpenNewFile(Playlist.Current, Playlist.CurrentEntry);
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

    private void HandleMouseMotion(SDL.SDL_Event ev)
    {
        _mouseX = ev.motion.x;
        _mouseY = ev.motion.y;

        if (_windowResizing)
        {
            int newW = _resizeStartW;
            int newH = _resizeStartH;
            // ev.motion.x/y are window-relative; during resize they represent current mouse pos
            _renderer.GetWindowPosition(out int curWinX, out int curWinY);
            int screenX = curWinX + ev.motion.x;
            int screenY = curWinY + ev.motion.y;

            if (_resizeEdge == 1 || _resizeEdge == 3) // Right or BottomRight
                newW = Math.Max(320, screenX - _resizeStartWinX);
            if (_resizeEdge == 2 || _resizeEdge == 3) // Bottom or BottomRight
                newH = Math.Max(240, screenY - _resizeStartWinY);

            _renderer.SetWindowGeometry(-1, -1, newW, newH);
            _forceRedraw = true;
            return;
        }

        if (_windowDragging)
        {
            _renderer.SetWindowPosition(ev.motion.x - _windowDragOffsetX, ev.motion.y - _windowDragOffsetY);
            return;
        }

        _renderer.SetTitleBarHover(ev.motion.x, ev.motion.y);
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
        else if (_plDragging && ev.motion.x < SDLRenderer.PlaylistPanelWidth)
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
    }

    private void HandleMouseWheel(SDL.SDL_Event ev)
    {
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
    }

    private bool HandlePlaylistPanelHit(SDLRenderer.PlaylistPanelHit plHit)
    {
        switch (plHit)
        {
            case SDLRenderer.PlaylistPanelHit.RepeatMode:
                CycleRepeatMode();
                _forceRedraw = true;
                return true;
            case SDLRenderer.PlaylistPanelHit.AddFile:
                var files = OpenFileDialogMultiple();
                ResyncClock();
                if (files != null && Playlist != null)
                {
                    foreach (var f in files) Playlist.Add(f);
                    _forceRedraw = true;
                }
                return true;
            case SDLRenderer.PlaylistPanelHit.AddFolder:
                string? folder = OpenFolderDialog();
                ResyncClock();
                if (folder != null && Playlist != null)
                {
                    Playlist.AddFolder(folder);
                    _forceRedraw = true;
                }
                return true;
            case SDLRenderer.PlaylistPanelHit.AddUrl:
                string? urlInput = OpenUrlDialog();
                ResyncClock();
                if (!string.IsNullOrWhiteSpace(urlInput) && Playlist != null)
                {
                    var lines = urlInput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !l.StartsWith("#")).ToList();
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        foreach (var line in lines)
                        {
                            var plugin = PluginLoader.FindPluginForUrl(line);
                            if (plugin != null)
                            {
                                Console.Error.WriteLine($"[{plugin.Name}] Ekstrakcja linków z: {line}");
                                var entries = plugin.Resolve(line);
                                lock (Playlist)
                                {
                                    foreach (var e in entries)
                                        Playlist.Add(e);
                                }
                                Console.Error.WriteLine($"[{plugin.Name}] Dodano {entries.Count} link(ów) HLS");
                            }
                            else if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                     line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                lock (Playlist)
                                {
                                    Playlist.Add(line);
                                }
                            }
                        }
                        _forceRedraw = true;
                    });
                }
                return true;
            case SDLRenderer.PlaylistPanelHit.ClearPlaylist:
                if (Playlist != null)
                {
                    Playlist.Clear();
                    _renderer.PlaylistSelectedIndex = -1;
                    _paused = true;
                    _clock.Stop();
                    _decodeRunning = false;
                    _renderer.StopAudio();
                    lock (_videoLock) { Monitor.PulseAll(_videoLock); }
                    lock (_audioLock) { Monitor.PulseAll(_audioLock); }
                    if (_decodeThread != null && _decodeThread.IsAlive)
                        _decodeThread.Join(3000);
                    _decoder.Dispose();
                    _clockStarted = false;
                    _trackEnded = true;
                    _trackEof = false;
                    _duration = 0;
                    ClearQueues();
                    _lastFrame?.Dispose();
                    _lastFrame = null;
                    _renderer.Duration = 0;
                    _renderer.SetProgress(0);
                    _forceRedraw = true;
                }
                return true;
            case SDLRenderer.PlaylistPanelHit.ItemClick:
                int idx = _renderer.PlaylistPanelClickedItem;
                if (Playlist != null && idx >= 0 && idx < Playlist.Count)
                {
                    _plDragFromIndex = idx;
                    _plDragging = true;
                    _renderer.DragFromIndex = idx;
                    _renderer.DragToIndex = idx;
                    _forceRedraw = true;
                }
                return true;
        }
        return false;
    }

    private void HandleControlButton(SDLRenderer.ControlButton btn)
    {
        switch (btn)
        {
            case SDLRenderer.ControlButton.Prev:
                _pendingPrevNext = -1;
                break;
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
            case SDLRenderer.ControlButton.Next:
                _pendingPrevNext = 1;
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

    private int ProgressBarMargin => _renderer.PlaylistPanelVisible ? SDLRenderer.PlaylistPanelWidth + 20 : 20;

    private bool IsOnProgressBar(int x, int y)
    {
        int barY = _renderer.GetProgressBarY();
        int margin = ProgressBarMargin;
        return y >= barY && y <= _renderer.GetWindowHeight() && x >= margin;
    }

    private void HandleSeekByMouse(int mouseX)
    {
        int winW = _renderer.GetWindowWidth();
        int margin = ProgressBarMargin;
        double frac = Math.Clamp((double)(mouseX - margin) / (winW - margin - 20), 0, 1);
        double target = frac * _duration;
        _renderer.SetProgress(target);
        RequestSeek(target);
    }

    private void RequestSeek(double target)
    {
        _seekTarget = target;
        _seekRequested = true;
    }
}
