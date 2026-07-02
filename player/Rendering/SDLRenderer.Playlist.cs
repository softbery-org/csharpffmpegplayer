using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    public int PlaylistSelectedIndex = -1;
    public int PlaylistHoverIndex = -1;
    private const int PlaylistHeaderH = 70;

    // Search state
    public bool PlaylistSearchActive = false;
    public string PlaylistSearchQuery = "";
    private int[] _filteredIndices = Array.Empty<int>();
    private SDL.SDL_Rect _btnSearch;
    private SDL.SDL_Rect _searchInputRect;
    private int _searchCursorBlink = 0;

    public void ScrollPlaylist(int delta)
    {
        int totalItems = GetFilteredCount();
        if (totalItems == 0 || _windowH <= 0) return;
        int headerH = PlaylistSearchActive ? 100 : PlaylistHeaderH;
        int panelH      = _windowH;
        int visibleItems = Math.Max(1, (panelH - headerH) / PlaylistItemHeight);
        int maxOffset   = Math.Max(0, totalItems - visibleItems);
        _playlistScrollOffset = Math.Clamp(_playlistScrollOffset + delta, 0, maxOffset);
    }

    public void ScrollPlaylistToIndex(int index)
    {
        if (index < 0 || _windowH <= 0) return;
        int headerH = PlaylistSearchActive ? 100 : PlaylistHeaderH;
        int panelH = _windowH;
        int visibleItems = (panelH - headerH) / PlaylistItemHeight;
        if (index < _playlistScrollOffset)
            _playlistScrollOffset = index;
        else if (index >= _playlistScrollOffset + visibleItems)
            _playlistScrollOffset = index - visibleItems + 1;
    }

    public bool ServerConnected = false;

    public enum PlaylistPanelHit { None, RepeatMode, AddFile, AddFolder, AddUrl, BrowseServer, DisconnectServer, ClearPlaylist, Search, FilterByYear, ItemClick }
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
        if (Contains(_btnServer,    x, y)) return PlaylistPanelHit.BrowseServer;
        if (ServerConnected && Contains(_btnDisconnect, x, y)) return PlaylistPanelHit.DisconnectServer;
        if (ServerConnected && Contains(_btnFilter, x, y)) return PlaylistPanelHit.FilterByYear;
        if (Contains(_btnClear,     x, y)) return PlaylistPanelHit.ClearPlaylist;
        if (Contains(_btnSearch,    x, y)) return PlaylistPanelHit.Search;
        if (PlaylistSearchActive && Contains(_searchInputRect, x, y)) return PlaylistPanelHit.Search;
        if (y >= 70)
        {
            int headerH = PlaylistSearchActive ? 100 : 70;
            if (y >= headerH)
            {
                int vi = _playlistScrollOffset + (y - headerH) / PlaylistItemHeight;
                int totalItems = GetFilteredCount();
                if (vi >= 0 && vi < totalItems)
                {
                    PlaylistPanelClickedItem = MapFilteredToReal(vi);
                    return PlaylistPanelHit.ItemClick;
                }
            }
        }
        return PlaylistPanelHit.None;
    }

    public int GetPlaylistItemIndexAt(int y)
    {
        int headerH = PlaylistSearchActive ? 100 : PlaylistHeaderH;
        if (y < headerH) return -1;
        int vi = _playlistScrollOffset + (y - headerH) / PlaylistItemHeight;
        int totalItems = GetFilteredCount();
        if (vi < 0 || vi >= totalItems) return -1;
        return MapFilteredToReal(vi);
    }

    public int GetPlaylistDropIndex(int y)
    {
        int headerH = PlaylistSearchActive ? 100 : PlaylistHeaderH;
        if (y < headerH) return 0;
        int panelH = _windowH;
        if (y >= panelH) return _playlistNames.Length;
        int relY = y - headerH;
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
        UpdateSearchFilter();
    }

    public void UpdateSearchFilter()
    {
        if (!PlaylistSearchActive || string.IsNullOrEmpty(PlaylistSearchQuery))
        {
            _filteredIndices = Array.Empty<int>();
            return;
        }
        var query = PlaylistSearchQuery.ToLowerInvariant();
        var matches = new List<int>();
        for (int i = 0; i < _playlistNames.Length; i++)
        {
            if (_playlistNames[i].ToLowerInvariant().Contains(query))
                matches.Add(i);
        }
        _filteredIndices = matches.ToArray();
        _playlistScrollOffset = 0;
    }

    public int GetFilteredCount()
    {
        return PlaylistSearchActive && _filteredIndices.Length > 0 ? _filteredIndices.Length : _playlistNames.Length;
    }

    public int MapFilteredToReal(int filteredIdx)
    {
        if (!PlaylistSearchActive || _filteredIndices.Length == 0)
            return filteredIdx;
        if (filteredIdx < 0 || filteredIdx >= _filteredIndices.Length)
            return -1;
        return _filteredIndices[filteredIdx];
    }

    public int MapRealToFiltered(int realIdx)
    {
        if (!PlaylistSearchActive || _filteredIndices.Length == 0)
            return realIdx;
        return Array.IndexOf(_filteredIndices, realIdx);
    }

    private void DrawPlaylistPanel()
    {
        if (_font == IntPtr.Zero) return;

        int panelH = _windowH;
        int panelX = 0;
        int panelW = PlaylistPanelWidth;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
        SDL.SDL_SetRenderDrawColor(_renderer, 15, 15, 20, 210);
        var bg = new SDL.SDL_Rect { x = panelX, y = 0, w = panelW, h = panelH };
        SDL.SDL_RenderFillRect(_renderer, ref bg);

        SDL.SDL_SetRenderDrawColor(_renderer, 35, 35, 50, 255);
        var header = new SDL.SDL_Rect { x = panelX, y = 0, w = panelW, h = 34 };
        SDL.SDL_RenderFillRect(_renderer, ref header);

        SDL.SDL_SetRenderDrawColor(_renderer, 25, 25, 38, 255);
        var btnBar = new SDL.SDL_Rect { x = panelX, y = 34, w = panelW, h = 34 };
        SDL.SDL_RenderFillRect(_renderer, ref btnBar);

        SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 80, 200);
        SDL.SDL_RenderDrawLine(_renderer, panelX, 68, panelX + panelW - 1, 68);

        SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 80, 200);
        SDL.SDL_RenderDrawLine(_renderer, panelX + panelW - 1, 0, panelX + panelW - 1, panelH);

        RenderTextInPanel("Playlista", panelW / 2, 9, new SDL.SDL_Color { r = 200, g = 200, b = 220, a = 255 }, center: true);

        // Repeat mode button
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

        int repeatLabelW = 90;
        if (_font != IntPtr.Zero)
            SDLTtf.TTF_SizeUTF8(_font, repeatLabel, out repeatLabelW, out _);

        _btnRepeat = new SDL.SDL_Rect { x = panelX + 6, y = 37, w = repeatLabelW + 16, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 40, 50, 70, 180);
        SDL.SDL_RenderFillRect(_renderer, ref _btnRepeat);
        SDL.SDL_SetRenderDrawColor(_renderer, 70, 90, 120, 200);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnRepeat);
        RenderTextInPanel(repeatLabel, _btnRepeat.x + 8, _btnRepeat.y + 5, repeatColor);

        // Add URL button
        _btnAddUrl = new SDL.SDL_Rect { x = panelX + panelW - 238, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 70, 40, 90, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnAddUrl);
        SDL.SDL_SetRenderDrawColor(_renderer, 150, 90, 200, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnAddUrl);
        RenderTextInPanel("URL", _btnAddUrl.x + 3, _btnAddUrl.y + 4,
            new SDL.SDL_Color { r = 210, g = 170, b = 255, a = 255 });

        // Server button
        _btnServer = new SDL.SDL_Rect { x = panelX + panelW - 204, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 40, 60, 80, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnServer);
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 120, 180, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnServer);
        RenderTextInPanel("SRV", _btnServer.x + 3, _btnServer.y + 4,
            new SDL.SDL_Color { r = 140, g = 200, b = 255, a = 255 });

        // Disconnect button (only when connected)
        if (ServerConnected)
        {
            _btnDisconnect = new SDL.SDL_Rect { x = panelX + panelW - 170, y = 37, w = 30, h = 26 };
            SDL.SDL_SetRenderDrawColor(_renderer, 80, 40, 30, 200);
            SDL.SDL_RenderFillRect(_renderer, ref _btnDisconnect);
            SDL.SDL_SetRenderDrawColor(_renderer, 180, 80, 60, 220);
            SDL.SDL_RenderDrawRect(_renderer, ref _btnDisconnect);
            RenderTextInPanel("DC", _btnDisconnect.x + 5, _btnDisconnect.y + 4,
                new SDL.SDL_Color { r = 255, g = 160, b = 120, a = 255 });
        }

        // Search button
        _btnSearch = new SDL.SDL_Rect { x = panelX + panelW - 136, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer,
            PlaylistSearchActive ? (byte)60 : (byte)30,
            PlaylistSearchActive ? (byte)80 : (byte)50,
            PlaylistSearchActive ? (byte)120 : (byte)80, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnSearch);
        SDL.SDL_SetRenderDrawColor(_renderer,
            PlaylistSearchActive ? (byte)120 : (byte)80,
            PlaylistSearchActive ? (byte)180 : (byte)120,
            PlaylistSearchActive ? (byte)255 : (byte)180, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnSearch);
        RenderTextInPanel("\uD83D\uDD0D", _btnSearch.x + 8, _btnSearch.y + 4,
            new SDL.SDL_Color { r = PlaylistSearchActive ? (byte)180 : (byte)140, g = PlaylistSearchActive ? (byte)220 : (byte)180, b = 255, a = 255 });

        // Add folder button
        _btnAddFolder = new SDL.SDL_Rect { x = panelX + panelW - 102, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 50, 60, 100, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnAddFolder);
        SDL.SDL_SetRenderDrawColor(_renderer, 90, 110, 180, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnAddFolder);
        RenderTextInPanel("DIR", _btnAddFolder.x + 3, _btnAddFolder.y + 4,
            new SDL.SDL_Color { r = 160, g = 180, b = 255, a = 255 });

        // Filter button (only when connected)
        if (ServerConnected)
        {
            _btnFilter = new SDL.SDL_Rect { x = panelX + panelW - 136, y = 37, w = 30, h = 26 };
            SDL.SDL_SetRenderDrawColor(_renderer, 40, 70, 50, 200);
            SDL.SDL_RenderFillRect(_renderer, ref _btnFilter);
            SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 100, 220);
            SDL.SDL_RenderDrawRect(_renderer, ref _btnFilter);
            RenderTextInPanel("FLT", _btnFilter.x + 3, _btnFilter.y + 4,
                new SDL.SDL_Color { r = 140, g = 220, b = 160, a = 255 });
        }

        // Clear playlist button
        _btnClear = new SDL.SDL_Rect { x = panelX + panelW - 68, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 30, 30, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnClear);
        SDL.SDL_SetRenderDrawColor(_renderer, 160, 60, 60, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnClear);
        RenderTextInPanel("CLR", _btnClear.x + 4, _btnClear.y + 4,
            new SDL.SDL_Color { r = 255, g = 120, b = 120, a = 255 });

        // Add file button
        _btnAdd = new SDL.SDL_Rect { x = panelX + panelW - 34, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 40, 80, 50, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnAdd);
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 90, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnAdd);
        RenderTextInPanel("+", _btnAdd.x + _btnAdd.w / 2, _btnAdd.y + 4,
            new SDL.SDL_Color { r = 140, g = 230, b = 140, a = 255 }, center: true);

        // Search input bar (when active)
        int headerH = 70;
        if (PlaylistSearchActive)
        {
            headerH = 100;
            _searchInputRect = new SDL.SDL_Rect { x = panelX + 6, y = 70, w = panelW - 12, h = 26 };
            SDL.SDL_SetRenderDrawColor(_renderer, 20, 25, 40, 255);
            SDL.SDL_RenderFillRect(_renderer, ref _searchInputRect);
            SDL.SDL_SetRenderDrawColor(_renderer, 80, 120, 180, 220);
            SDL.SDL_RenderDrawRect(_renderer, ref _searchInputRect);

            string searchText = PlaylistSearchQuery;
            int searchX = _searchInputRect.x + 8;
            int searchY = _searchInputRect.y + 5;
            var searchColor = new SDL.SDL_Color { r = 200, g = 200, b = 220, a = 255 };
            if (!string.IsNullOrEmpty(searchText))
            {
                RenderTextInPanel(searchText, searchX, searchY, searchColor);
            }
            else
            {
                RenderTextInPanel("Szukaj...", searchX, searchY,
                    new SDL.SDL_Color { r = 100, g = 100, b = 120, a = 180 });
            }

            // Blinking cursor
            _searchCursorBlink++;
            if ((_searchCursorBlink / 15) % 2 == 0)
            {
                int cursorX = searchX;
                if (!string.IsNullOrEmpty(searchText) && _font != IntPtr.Zero)
                {
                    SDLTtf.TTF_SizeUTF8(_font, searchText, out int tw, out _);
                    cursorX += tw;
                }
                SDL.SDL_SetRenderDrawColor(_renderer, 200, 220, 255, 255);
                SDL.SDL_RenderDrawLine(_renderer, cursorX + 1, searchY, cursorX + 1, searchY + 16);
            }

            // Show result count
            int filteredCount = _filteredIndices.Length;
            string countText = $"{filteredCount} wynik(\u00F3w)";
            RenderTextInPanel(countText, panelW - 80, searchY,
                new SDL.SDL_Color { r = 100, g = 160, b = 220, a = 200 });

            SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 80, 200);
            SDL.SDL_RenderDrawLine(_renderer, panelX, headerH - 2, panelX + panelW - 1, headerH - 2);
        }

        // Determine which items to show: filtered or all
        int totalItems = GetFilteredCount();
        int visibleItems = (panelH - headerH) / PlaylistItemHeight;
        int maxOffset = Math.Max(0, totalItems - visibleItems);
        _playlistScrollOffset = Math.Clamp(_playlistScrollOffset, 0, maxOffset);

        for (int vi = _playlistScrollOffset; vi < totalItems && vi < _playlistScrollOffset + visibleItems; vi++)
        {
            int i = MapFilteredToReal(vi);
            if (i < 0) continue;
            int itemY = headerH + (vi - _playlistScrollOffset) * PlaylistItemHeight;
            bool isCurrent  = i == _playlistCurrentIndex;
            bool isDragging = i == DragFromIndex && DragFromIndex >= 0;
            int selReal = PlaylistSearchActive ? MapFilteredToReal(PlaylistSelectedIndex) : PlaylistSelectedIndex;
            bool isSelected = i == selReal && selReal >= 0;
            bool isHovered  = i == PlaylistHoverIndex && PlaylistHoverIndex >= 0 && !isCurrent;

            if (isDragging)
            {
                SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 60, 120);
                var dimBg = new SDL.SDL_Rect { x = panelX, y = itemY, w = panelW, h = PlaylistItemHeight };
                SDL.SDL_RenderFillRect(_renderer, ref dimBg);
            }

            if (isHovered && !isDragging)
            {
                SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);
                SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, 18);
                var hovBg = new SDL.SDL_Rect { x = panelX, y = itemY, w = panelW, h = PlaylistItemHeight };
                SDL.SDL_RenderFillRect(_renderer, ref hovBg);
                SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
            }

            if (isCurrent && !isDragging)
            {
                SDL.SDL_SetRenderDrawColor(_renderer, 180, 90, 10, 200);
                var itemBg = new SDL.SDL_Rect { x = panelX + 2, y = itemY + 1, w = panelW - 4, h = PlaylistItemHeight - 2 };
                SDL.SDL_RenderFillRect(_renderer, ref itemBg);
                SDL.SDL_SetRenderDrawColor(_renderer, 255, 140, 0, 255);
                var accent = new SDL.SDL_Rect { x = panelX, y = itemY + 1, w = 3, h = PlaylistItemHeight - 2 };
                SDL.SDL_RenderFillRect(_renderer, ref accent);
            }

            if (isSelected && !isDragging)
            {
                SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, 200);
                var sel = new SDL.SDL_Rect { x = panelX + 2, y = itemY + 1, w = panelW - 4, h = PlaylistItemHeight - 2 };
                SDL.SDL_RenderDrawRect(_renderer, ref sel);
            }

            SDL.SDL_SetRenderDrawColor(_renderer, 45, 45, 60, 150);
            SDL.SDL_RenderDrawLine(_renderer, panelX + 8, itemY + PlaylistItemHeight - 1, panelX + panelW - 8, itemY + PlaylistItemHeight - 1);

            int textY = itemY + (PlaylistItemHeight - 18) / 2;

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

        // Drop indicator
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
}
