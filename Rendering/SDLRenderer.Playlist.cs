using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    public int PlaylistSelectedIndex = -1;
    public int PlaylistHoverIndex = -1;
    private const int PlaylistHeaderH = 70;

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

    public int GetPlaylistItemIndexAt(int y)
    {
        if (y < PlaylistHeaderH) return -1;
        int idx = _playlistScrollOffset + (y - PlaylistHeaderH) / PlaylistItemHeight;
        if (idx < 0 || idx >= _playlistNames.Length) return -1;
        return idx;
    }

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
        _btnAddUrl = new SDL.SDL_Rect { x = panelX + panelW - 144, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 70, 40, 90, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnAddUrl);
        SDL.SDL_SetRenderDrawColor(_renderer, 150, 90, 200, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnAddUrl);
        RenderTextInPanel("URL", _btnAddUrl.x + 3, _btnAddUrl.y + 4,
            new SDL.SDL_Color { r = 210, g = 170, b = 255, a = 255 });

        // Clear playlist button
        _btnClear = new SDL.SDL_Rect { x = panelX + panelW - 108, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 30, 30, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnClear);
        SDL.SDL_SetRenderDrawColor(_renderer, 160, 60, 60, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnClear);
        RenderTextInPanel("CLR", _btnClear.x + 4, _btnClear.y + 4,
            new SDL.SDL_Color { r = 255, g = 120, b = 120, a = 255 });

        // Add folder button
        _btnAddFolder = new SDL.SDL_Rect { x = panelX + panelW - 72, y = 37, w = 30, h = 26 };
        SDL.SDL_SetRenderDrawColor(_renderer, 50, 60, 100, 200);
        SDL.SDL_RenderFillRect(_renderer, ref _btnAddFolder);
        SDL.SDL_SetRenderDrawColor(_renderer, 90, 110, 180, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _btnAddFolder);
        RenderTextInPanel("DIR", _btnAddFolder.x + 3, _btnAddFolder.y + 4,
            new SDL.SDL_Color { r = 160, g = 180, b = 255, a = 255 });

        // Add file button
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
