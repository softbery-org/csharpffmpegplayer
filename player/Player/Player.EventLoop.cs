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
                    case SDL.SDL_EventType.SDL_TEXTINPUT:
                        unsafe
                        {
                            byte* p = ev.text.text;
                            int len = 0;
                            while (p[len] != 0) len++;
                            HandleTextInput(System.Text.Encoding.UTF8.GetString(p, len));
                        }
                        break;
                }
            }

            if (_windowDragging)
            {
                _renderer.GetWindowPosition(out int winX, out int winY);
                _renderer.SetWindowPosition(winX + _mouseX - _windowDragOffsetX, winY + _mouseY - _windowDragOffsetY);
            }

            _renderer.IsPlaying = !_paused;
            _renderer.SetProgress(GetMasterClock());
            UpdatePlaylistPanel();

            if (!_paused)
                TryRenderNextFrame();
            else if (_forceRedraw)
                RedrawLastFrame();

            // Force redraw when no video is playing but UI needs updating
            // (e.g. after adding items from server in background thread)
            if (_forceRedraw && _decoder.VideoStreamIndex < 0)
            {
                _forceRedraw = false;
                _renderer.RenderUI();
            }

            UpdateSubtitle();

            if (_pendingOpenFile != null)
            {
                string file = _pendingOpenFile;
                var entry = _pendingOpenEntry;
                _pendingOpenFile = null;
                _pendingOpenEntry = null;
                if (file == "separator://" && Playlist != null && Playlist.Count > 1)
                {
                    // Skip separator entries — advance to next
                    Playlist.AdvanceToNext();
                    var next = Playlist.Current;
                    if (!string.IsNullOrWhiteSpace(next) && next != "separator://")
                    {
                        _pendingOpenFile = next;
                        _pendingOpenEntry = Playlist.CurrentEntry;
                    }
                }
                else if (file.StartsWith("serverplaylist://") || file.StartsWith("serverseason://") || file.StartsWith("serversource://") || file.StartsWith("servermoviesource://") || file.StartsWith("serveryear://") || file.StartsWith("servermediatype://") || file == "serverback://" || file == "serverseries://")
                {
                    // Navigation entries are handled by OpenNewFile which returns false
                    // Don't re-queue — OpenNewFile already triggered the load
                }
                else if (!OpenNewFile(file, entry))
                {
                    // Open is busy; re-queue for later (but not if empty/separator)
                    if (!string.IsNullOrWhiteSpace(file) && file != "separator://")
                    {
                        _pendingOpenFile = file;
                        _pendingOpenEntry = entry;
                    }
                }
            }

            ProcessAsyncOpenCompletion();
            ProcessApiRequests();

            if (_pendingPrevNext != 0 && Playlist != null)
            {
                int dir = _pendingPrevNext;
                _pendingPrevNext = 0;
                bool advanced = dir > 0 ? Playlist.AdvanceToNext() : Playlist.AdvanceToPrev();
                if (advanced && !OpenNewFile(Playlist.Current, Playlist.CurrentEntry))
                {
                    // Open is busy; revert index and re-queue the request
                    if (dir > 0) Playlist.AdvanceToPrev(); else Playlist.AdvanceToNext();
                    _pendingPrevNext = dir;
                }
            }

            if (_stopRequested)
            {
                _stopRequested = false;
                SetPaused(true);
                ClearQueues();
                _renderer.SetProgress(0);
            }

            long trackAge = Environment.TickCount64 - _trackOpenTicks;
            long sinceReopen = Environment.TickCount64 - _lastReopenTicks;
            bool eofDrained = _trackEof && _clockStarted && _audioQueue.Count == 0 && _pendingOpenFile == null
                              && !_asyncOpenPending && trackAge > 3000 && sinceReopen > 5000;
            bool clockEnd   = _clockStarted && _duration > 1.0 && GetMasterClock() >= _duration - 0.15;
            if (!_trackEnded && (clockEnd || eofDrained))
            {
                Console.Error.WriteLine($"[Player] Track end: clockEnd={clockEnd} eofDrained={eofDrained} " +
                    $"clock={GetMasterClock():F1}s dur={_duration:F1}s trackEof={_trackEof} " +
                    $"clockStarted={_clockStarted} audioQueue={_audioQueue.Count} " +
                    $"trackAge={trackAge} sinceReopen={sinceReopen}");
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
                        SetPaused(true);
                        Console.Error.WriteLine("[Player] Playlist ended");
                    }
                }
                else
                {
                    SetPaused(true);
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
                if (_renderer.LoginVisible)
                {
                    CloseLoginDialog();
                    break;
                }
                if (_renderer.PlaylistSearchActive)
                {
                    _renderer.PlaylistSearchActive = false;
                    _renderer.PlaylistSearchQuery = "";
                    _renderer.UpdateSearchFilter();
                    _renderer.PlaylistSelectedIndex = -1;
                    SDL.SDL_StopTextInput();
                    _forceRedraw = true;
                    break;
                }
                if (_renderer.AboutVisible)
                {
                    _renderer.AboutVisible = false;
                    _forceRedraw = true;
                }
                else if (_renderer.SeriesInfoVisible)
                {
                    _renderer.SeriesInfoVisible = false;
                    _renderer.ClearSeriesPoster();
                    _forceRedraw = true;
                }
                else if (_renderer.EpisodeInfoVisible)
                {
                    _renderer.EpisodeInfoVisible = false;
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
                SetPaused(!_paused);
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
            case SDL.SDL_Keycode.SDLK_t:
                _renderer.SetAlwaysOnTop(!_renderer.AlwaysOnTop);
                _forceRedraw = true;
                break;
            case SDL.SDL_Keycode.SDLK_f:
                ToggleFullscreen();
                break;
            case SDL.SDL_Keycode.SDLK_TAB:
                if (_renderer.LoginVisible)
                {
                    _renderer.LoginFieldFocus = (_renderer.LoginFieldFocus + 1) % 3;
                    _forceRedraw = true;
                    break;
                }
                _renderer.PlaylistPanelVisible = !_renderer.PlaylistPanelVisible;
                if (_renderer.PlaylistPanelVisible && Playlist != null)
                    _renderer.PlaylistSelectedIndex = Playlist.CurrentIndex;
                else
                {
                    _renderer.PlaylistSelectedIndex = -1;
                    _renderer.PlaylistSearchActive = false;
                    _renderer.PlaylistSearchQuery = "";
                    SDL.SDL_StopTextInput();
                }
                _forceRedraw = true;
                break;
            case SDL.SDL_Keycode.SDLK_SLASH:
                if (_renderer.PlaylistPanelVisible && !_renderer.PlaylistSearchActive)
                {
                    _renderer.PlaylistSearchActive = true;
                    _renderer.PlaylistSearchQuery = "";
                    _renderer.UpdateSearchFilter();
                    _renderer.PlaylistSelectedIndex = -1;
                    SDL.SDL_StartTextInput();
                    _forceRedraw = true;
                }
                break;
            case SDL.SDL_Keycode.SDLK_UP:
                if (_renderer.PlaylistPanelVisible && Playlist != null && Playlist.Count > 0)
                {
                    if (_renderer.PlaylistSearchActive)
                    {
                        int max = _renderer.GetFilteredCount();
                        int sel = _renderer.PlaylistSelectedIndex < 0 ? 0 : _renderer.PlaylistSelectedIndex;
                        _renderer.PlaylistSelectedIndex = Math.Max(0, sel - 1);
                        _renderer.ScrollPlaylistToIndex(_renderer.PlaylistSelectedIndex);
                        TryShowEpisodeInfo();
                        _forceRedraw = true;
                    }
                    else
                    {
                        int sel = _renderer.PlaylistSelectedIndex < 0 ? Playlist.CurrentIndex : _renderer.PlaylistSelectedIndex;
                        _renderer.PlaylistSelectedIndex = Math.Max(0, sel - 1);
                        ScrollPlaylistToSelected();
                        TryShowEpisodeInfo();
                        _forceRedraw = true;
                    }
                }
                break;
            case SDL.SDL_Keycode.SDLK_DOWN:
                if (_renderer.PlaylistPanelVisible && Playlist != null && Playlist.Count > 0)
                {
                    if (_renderer.PlaylistSearchActive)
                    {
                        int max = _renderer.GetFilteredCount();
                        int sel = _renderer.PlaylistSelectedIndex < 0 ? 0 : _renderer.PlaylistSelectedIndex;
                        _renderer.PlaylistSelectedIndex = Math.Min(max - 1, sel + 1);
                        _renderer.ScrollPlaylistToIndex(_renderer.PlaylistSelectedIndex);
                        TryShowEpisodeInfo();
                        _forceRedraw = true;
                    }
                    else
                    {
                        int sel = _renderer.PlaylistSelectedIndex < 0 ? Playlist.CurrentIndex : _renderer.PlaylistSelectedIndex;
                        _renderer.PlaylistSelectedIndex = Math.Min(Playlist.Count - 1, sel + 1);
                        ScrollPlaylistToSelected();
                        TryShowEpisodeInfo();
                        _forceRedraw = true;
                    }
                }
                break;
            case SDL.SDL_Keycode.SDLK_RETURN:
            case SDL.SDL_Keycode.SDLK_KP_ENTER:
                if (_renderer.LoginVisible)
                {
                    TryServerLogin();
                    break;
                }
                if (_renderer.PlaylistPanelVisible && Playlist != null)
                {
                    int idx;
                    if (_renderer.PlaylistSearchActive)
                    {
                        idx = _renderer.MapFilteredToReal(_renderer.PlaylistSelectedIndex);
                        if (idx >= 0 && idx < Playlist.Count)
                        {
                            _renderer.PlaylistSearchActive = false;
                            _renderer.PlaylistSearchQuery = "";
                            _renderer.UpdateSearchFilter();
                            SDL.SDL_StopTextInput();
                        }
                    }
                    else
                    {
                        idx = _renderer.PlaylistSelectedIndex;
                    }
                    if (idx >= 0 && idx < Playlist.Count)
                    {
                        Playlist.MoveTo(idx);
                        if (!OpenNewFile(Playlist.Current, Playlist.CurrentEntry))
                        {
                            // Open is busy — queue for retry (but not if empty/separator)
                            var cur = Playlist.Current;
                            if (!string.IsNullOrWhiteSpace(cur) && cur != "separator://")
                            {
                                _pendingOpenFile = cur;
                                _pendingOpenEntry = Playlist.CurrentEntry;
                            }
                        }
                    }
                }
                break;
            case SDL.SDL_Keycode.SDLK_DELETE:
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
                            SetPaused(true);
                        }
                        _forceRedraw = true;
                    }
                }
                break;
            case SDL.SDL_Keycode.SDLK_BACKSPACE:
                if (_renderer.LoginVisible)
                {
                    HandleLoginBackspace();
                    break;
                }
                if (_renderer.PlaylistSearchActive)
                {
                    if (_renderer.PlaylistSearchQuery.Length > 0)
                    {
                        _renderer.PlaylistSearchQuery = _renderer.PlaylistSearchQuery[..^1];
                        _renderer.UpdateSearchFilter();
                        _renderer.PlaylistSelectedIndex = -1;
                        _forceRedraw = true;
                    }
                    break;
                }
                if (_renderer.ServerConnected && Playlist != null && Playlist.Count > 0)
                {
                    // Check if we're in server navigation (has server entries)
                    bool hasServerEntries = false;
                    for (int i = 0; i < Playlist.Count; i++)
                    {
                        string u = Playlist.Entries[i].Url ?? "";
                        if (u.StartsWith("server") || u == "separator://")
                        {
                            hasServerEntries = true;
                            break;
                        }
                    }

                    if (hasServerEntries)
                    {
                        // Backspace = go back to server playlist list
                        _renderer.EpisodeInfoVisible = false;
                        _renderer.SeriesInfoVisible = false;
                        _renderer.ClearSeriesPoster();
                        _forceRedraw = true;
                        // Re-fetch playlist list from server
                        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                _renderer.ShowStatus("\u21BB Powrót do playlist...");
                                _forceRedraw = true;
                                var playlists = ServerClient.ListPlaylistsAsync().Result;
                                BuildServerPlaylistList(playlists);
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"[Server] Back error: {ex.Message}");
                                _renderer.ShowError($"Błąd: {ex.Message}");
                                _forceRedraw = true;
                            }
                        });
                    }
                    else
                    {
                        // Not in server navigation — fall through to delete
                        goto case SDL.SDL_Keycode.SDLK_DELETE;
                    }
                }
                else if (_renderer.PlaylistPanelVisible && Playlist != null)
                {
                    goto case SDL.SDL_Keycode.SDLK_DELETE;
                }
                break;
        }
    }

    private void HandleTextInput(string text)
    {
        if (_renderer.LoginVisible)
        {
            HandleLoginTextInput(text);
            return;
        }
        if (!_renderer.PlaylistSearchActive) return;
        if (string.IsNullOrEmpty(text)) return;
        _renderer.PlaylistSearchQuery += text;
        _renderer.UpdateSearchFilter();
        _renderer.PlaylistSelectedIndex = -1;
        _forceRedraw = true;
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
                _lastResizeTicks = 0;
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
        if (titleHit == SDLRenderer.TitleBarButton.Maximize)
        {
            if (_fullscreen)
            {
                _fullscreen = false;
                _renderer.SetFullscreen(false);
                if (!_renderer.IsMaximized)
                    _renderer.MaximizeWindow();
            }
            else
            {
                _renderer.MaximizeWindow();
            }
            return;
        }

        // Window dragging — click on empty title bar area
        if (_renderer.IsInTitleBarDragArea(ev.button.x, ev.button.y))
        {
            if (_fullscreen)
            {
                _fullscreen = false;
                _renderer.SetFullscreen(false);
            }
            _renderer.RestoreWindowForDrag(ev.button.x, ev.button.y);
            _windowDragging = true;
            _windowDragOffsetX = ev.button.x;
            _windowDragOffsetY = ev.button.y;
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

        // Login dialog clicks
        if (_renderer.LoginVisible)
        {
            var loginHit = _renderer.HitTestLogin(ev.button.x, ev.button.y);
            switch (loginHit)
            {
                case SDLRenderer.LoginHit.ServerField:
                    _renderer.LoginFieldFocus = 0;
                    SDL.SDL_StartTextInput();
                    break;
                case SDLRenderer.LoginHit.UserField:
                    _renderer.LoginFieldFocus = 1;
                    SDL.SDL_StartTextInput();
                    break;
                case SDLRenderer.LoginHit.PassField:
                    _renderer.LoginFieldFocus = 2;
                    SDL.SDL_StartTextInput();
                    break;
                case SDLRenderer.LoginHit.LoginBtn:
                    TryServerLogin();
                    break;
                case SDLRenderer.LoginHit.CancelBtn:
                    CloseLoginDialog();
                    break;
            }
            _forceRedraw = true;
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
            // Potential window drag from video area (windowed mode only)
            if (!_fullscreen)
            {
                if (_renderer.IsMaximized)
                    _renderer.RestoreWindowForDrag(ev.button.x, ev.button.y);
                _videoDragPending = true;
                _videoDragStartX = ev.button.x;
                _videoDragStartY = ev.button.y;
                _windowDragOffsetX = ev.button.x;
                _windowDragOffsetY = ev.button.y;
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
            _videoDragPending = false;
            return;
        }
        if (_videoDragPending)
        {
            _videoDragPending = false;
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
            long now = Environment.TickCount64;
            if (now - _lastResizeTicks < ResizeThrottleMs)
                return;
            _lastResizeTicks = now;

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
            _mouseX = ev.motion.x;
            _mouseY = ev.motion.y;
            return;
        }

        if (_videoDragPending)
        {
            const int threshold = 3;
            int dx = ev.motion.x - _videoDragStartX;
            int dy = ev.motion.y - _videoDragStartY;
            if (Math.Abs(dx) > threshold || Math.Abs(dy) > threshold)
                _windowDragging = true;
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
        _renderer.UpdateCursor(ev.motion.x, ev.motion.y);
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
                    int totalLines = lines.Count;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        int processed = 0;
                        foreach (var line in lines)
                        {
                            processed++;
                            var plugin = PluginLoader.FindPluginForUrl(line);

                            if (plugin is ISeriesPlugin seriesPlugin)
                            {
                                var episodes = seriesPlugin.GetEpisodes(line);
                                if (episodes.Count > 0)
                                {
                                    Console.Error.WriteLine($"[{plugin.Name}] Znaleziono {episodes.Count} odcinków dla: {line}");
                                    int epProcessed = 0;
                                    foreach (var ep in episodes)
                                    {
                                        epProcessed++;
                                        _renderer.ShowStatus(
                                            $"\u21BB Wczytywanie serialu {processed}/{totalLines}\n" +
                                            $"Odcinek {epProcessed}/{episodes.Count}: {ep.Title}");
                                        _forceRedraw = true;

                                        Console.Error.WriteLine($"[{plugin.Name}] Odcinek {epProcessed}/{episodes.Count}: {ep.Title}");
                                        var epEntries = seriesPlugin.ResolveEpisode(ep.Url, ep.Title);
                                        foreach (var e in epEntries)
                                        {
                                            lock (Playlist)
                                            {
                                                Playlist.Add(e);
                                            }
                                            _forceRedraw = true;
                                        }
                                        Console.Error.WriteLine($"[{plugin.Name}] Dodano {epEntries.Count} link(ów) dla odcinka {epProcessed}");
                                    }
                                    continue;
                                }
                            }

                            _renderer.ShowStatus($"\u21BB Wczytywanie linku {processed}/{totalLines}");
                            _forceRedraw = true;

                            if (plugin != null)
                            {
                                Console.Error.WriteLine($"[{plugin.Name}] Ekstrakcja linków z: {line}");
                                var entries = plugin.Resolve(line);
                                foreach (var e in entries)
                                {
                                    lock (Playlist)
                                    {
                                        Playlist.Add(e);
                                    }
                                    _forceRedraw = true;
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
                                _forceRedraw = true;
                            }
                        }
                        _renderer.ClearStatus();
                        _forceRedraw = true;
                    });
                }
                return true;
            case SDLRenderer.PlaylistPanelHit.BrowseServer:
                if (ServerClient == null || !ServerClient.IsConnected)
                {
                    OpenLoginDialog();
                    return true;
                }
                _renderer.ServerConnected = true;
                _forceRedraw = true;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        _renderer.ShowStatus("\u21BB Pobieranie playlist z serwera...");
                        _forceRedraw = true;

                        var playlists = ServerClient.ListPlaylistsAsync().Result;
                        Console.Error.WriteLine($"[Server] Pobrano {playlists.Count} playlist");

                        if (playlists.Count == 0)
                        {
                            _renderer.ShowStatus("Brak playlist na serwerze. Użyj import-series.");
                            _forceRedraw = true;
                            System.Threading.Thread.Sleep(3000);
                            _renderer.ClearStatus();
                            _forceRedraw = true;
                            return;
                        }

                        BuildServerPlaylistList(playlists);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Server] Błąd: {ex.Message}");
                        _renderer.ShowError($"Błąd serwera: {ex.Message}");
                        _forceRedraw = true;
                    }
                });
                return true;
            case SDLRenderer.PlaylistPanelHit.DisconnectServer:
                _renderer.ServerConnected = false;
                _renderer.SeriesInfoVisible = false;
                _renderer.EpisodeInfoVisible = false;
                ServerPlaylistCache.Clear();
                if (Playlist != null)
                {
                    Playlist.Clear();
                    _renderer.PlaylistSelectedIndex = -1;
                    Playlist.Reset();
                }
                _forceRedraw = true;
                _renderer.ShowStatus("Rozłączono z serwerem");
                _forceRedraw = true;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    System.Threading.Thread.Sleep(1500);
                    _renderer.ClearStatus();
                    _forceRedraw = true;
                });
                Console.Error.WriteLine("[Server] Rozłączono z serwerem");
                return true;
            case SDLRenderer.PlaylistPanelHit.FilterByYear:
                if (ServerClient == null || !ServerClient.IsConnected) return true;
                _forceRedraw = true;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        _renderer.ShowStatus("↻ Pobieranie filtrów...");
                        _forceRedraw = true;
                        var filters = ServerClient.GetFiltersAsync().Result;
                        if (filters == null || (filters.Years.Count == 0 && filters.MediaTypes.Count == 0))
                        {
                            _renderer.ShowStatus("Brak danych do filtrowania");
                            _forceRedraw = true;
                            System.Threading.Thread.Sleep(2000);
                            _renderer.ClearStatus();
                            _forceRedraw = true;
                            return;
                        }
                        lock (Playlist!)
                        {
                            Playlist.Clear();
                            Playlist.Add(new PlaylistEntry("serverback://", "← Wstecz do playlist", null));
                            // Media types
                            if (filters.MediaTypes.Count > 0)
                            {
                                Playlist.Add(new PlaylistEntry("separator://", "── Typ ──", null));
                                foreach (var mt in filters.MediaTypes)
                                {
                                    int count = ServerClient.ListMediaFilteredAsync(500, 0, year: null).Result.Count(m => m.MediaType == mt);
                                    Playlist.Add(new PlaylistEntry(
                                        $"servermediatype://{Uri.EscapeDataString(mt)}",
                                        $"🎬 {mt} ({count})",
                                        null));
                                }
                            }
                            // Years
                            if (filters.Years.Count > 0)
                            {
                                Playlist.Add(new PlaylistEntry("separator://", "── Rok ──", null));
                                foreach (var year in filters.Years)
                                {
                                    int count = ServerClient.ListMediaFilteredAsync(500, 0, year: year).Result.Count;
                                    Playlist.Add(new PlaylistEntry(
                                        $"serveryear://{year}",
                                        $"📅 {year} ({count})",
                                        null));
                                }
                            }
                        }
                        _renderer.PlaylistSelectedIndex = 1;
                        _forceRedraw = true;
                        _renderer.ShowStatus($"Filtry: {filters.Years.Count} lat, {filters.MediaTypes.Count} typów");
                        _forceRedraw = true;
                        System.Threading.Thread.Sleep(1500);
                        _renderer.ClearStatus();
                        _forceRedraw = true;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Server] Filter error: {ex.Message}");
                        _renderer.ShowError($"Błąd filtrów: {ex.Message}");
                        _forceRedraw = true;
                    }
                });
                return true;
            case SDLRenderer.PlaylistPanelHit.Search:
                if (_renderer.PlaylistSearchActive)
                {
                    _renderer.PlaylistSearchActive = false;
                    _renderer.PlaylistSearchQuery = "";
                    _renderer.UpdateSearchFilter();
                    _renderer.PlaylistSelectedIndex = -1;
                    SDL.SDL_StopTextInput();
                }
                else
                {
                    _renderer.PlaylistSearchActive = true;
                    _renderer.PlaylistSearchQuery = "";
                    _renderer.UpdateSearchFilter();
                    _renderer.PlaylistSelectedIndex = -1;
                    SDL.SDL_StartTextInput();
                }
                _forceRedraw = true;
                return true;
            case SDLRenderer.PlaylistPanelHit.ClearPlaylist:
                if (Playlist != null)
                {
                    // Cancel any pending async open to avoid race condition
                    _cancelOpen = true;
                    _asyncOpenPending = false;

                    Playlist.Clear();
                    _renderer.PlaylistSelectedIndex = -1;
                    SetPaused(true);
                    _decodeRunning = false;
                    _renderer.StopAudio();
                    lock (_videoLock) { Monitor.PulseAll(_videoLock); }
                    lock (_audioLock) { Monitor.PulseAll(_audioLock); }
                    if (_decodeThread != null && _decodeThread.IsAlive)
                        _decodeThread.Join(3000);
                    _decodeThread = null;

                    // Wait briefly for async open thread to see cancel
                    if (_asyncOpenRunning)
                    {
                        for (int i = 0; i < 300 && _asyncOpenRunning; i++)
                            Thread.Sleep(10);
                        if (_asyncOpenRunning)
                        {
                            Console.Error.WriteLine("[CLR] Async open still running, replacing decoder");
                            _decoder = new FFmpegDecoder { UseHwAccel = _useHwAccel };
                        }
                        else
                        {
                            try { _decoder.Dispose(); } catch { }
                        }
                    }
                    else
                    {
                        try { _decoder.Dispose(); } catch { }
                    }
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
                    RequestSeek(0);
                    _clock.Reset();
                    _clockStarted = false;
                    SetPaused(false);
                }
                else
                {
                    SetPaused(!_paused);
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

    private void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (_paused)
        {
            _clock.Stop();
            _renderer.PauseAudio();
        }
        else
        {
            _clock.Start();
            _renderer.ResumeAudio();
        }
    }

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

    private void HandleLoginTextInput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        switch (_renderer.LoginFieldFocus)
        {
            case 0:
                if (_renderer.LoginServerUrl.Length < 200)
                    _renderer.LoginServerUrl += text;
                break;
            case 1:
                if (_renderer.LoginUsername.Length < 64)
                    _renderer.LoginUsername += text;
                break;
            case 2:
                if (_renderer.LoginPassword.Length < 128)
                    _renderer.LoginPassword += text;
                break;
        }
        _forceRedraw = true;
    }

    private void HandleLoginBackspace()
    {
        switch (_renderer.LoginFieldFocus)
        {
            case 0:
                if (_renderer.LoginServerUrl.Length > 0)
                    _renderer.LoginServerUrl = _renderer.LoginServerUrl[..^1];
                break;
            case 1:
                if (_renderer.LoginUsername.Length > 0)
                    _renderer.LoginUsername = _renderer.LoginUsername[..^1];
                break;
            case 2:
                if (_renderer.LoginPassword.Length > 0)
                    _renderer.LoginPassword = _renderer.LoginPassword[..^1];
                break;
        }
        _forceRedraw = true;
    }

    public void OpenLoginDialog()
    {
        _renderer.LoginVisible = true;
        _renderer.LoginError = "";
        _renderer.LoginInProgress = false;

        // Pre-fill from saved credentials
        var savedCreds = ServerCredentialsManager.Load();
        if (savedCreds != null)
        {
            _renderer.LoginServerUrl = savedCreds.ServerUrl;
            _renderer.LoginUsername = savedCreds.Username;
            _renderer.LoginFieldFocus = 2; // Focus password
        }
        else if (ServerClient != null && !string.IsNullOrEmpty(ServerClient.ServerUrl))
        {
            _renderer.LoginServerUrl = ServerClient.ServerUrl;
            _renderer.LoginFieldFocus = 1;
        }
        else
        {
            _renderer.LoginServerUrl = "http://localhost:8800";
            _renderer.LoginFieldFocus = 0;
        }
        _renderer.LoginPassword = "";
        SDL.SDL_StartTextInput();
        _forceRedraw = true;
    }

    private void CloseLoginDialog()
    {
        _renderer.LoginVisible = false;
        _renderer.LoginError = "";
        _renderer.LoginInProgress = false;
        SDL.SDL_StopTextInput();
        _forceRedraw = true;
    }

    private void TryServerLogin()
    {
        string serverUrl = _renderer.LoginServerUrl.Trim();
        string username = _renderer.LoginUsername.Trim();
        string password = _renderer.LoginPassword;

        if (string.IsNullOrEmpty(serverUrl))
        {
            _renderer.LoginError = "Podaj adres serwera";
            _forceRedraw = true;
            return;
        }
        if (string.IsNullOrEmpty(username))
        {
            _renderer.LoginError = "Podaj nazwe uzytkownika";
            _forceRedraw = true;
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            _renderer.LoginError = "Podaj haslo";
            _forceRedraw = true;
            return;
        }

        _renderer.LoginError = "";
        _renderer.LoginInProgress = true;
        _forceRedraw = true;

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (ServerClient == null)
                    ServerClient = new MediaServerClient();

                if (ServerClient.ServerUrl != serverUrl)
                    ServerClient.Configure(serverUrl);

                bool ok = ServerClient.LoginAsync(username, password).Result;
                if (ok)
                {
                    Console.Error.WriteLine($"[Server] Logged in as {username} to {serverUrl}");
                    ServerCredentialsManager.Save(new ServerCredentials
                    {
                        ServerUrl = serverUrl,
                        Username = username,
                        ApiKey = ServerClient.GetApiKey(),
                    });
                    _renderer.LoginInProgress = false;
                    _renderer.LoginVisible = false;
                    _renderer.ServerConnected = true;
                    SDL.SDL_StopTextInput();
                    _forceRedraw = true;
                }
                else
                {
                    _renderer.LoginInProgress = false;
                    _renderer.LoginError = "Logowanie nieudane — bledny login lub haslo";
                    _forceRedraw = true;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Server] Login error: {ex.Message}");
                _renderer.LoginInProgress = false;
                _renderer.LoginError = $"Blad polaczenia: {ex.Message}";
                _forceRedraw = true;
            }
        });
    }
}
