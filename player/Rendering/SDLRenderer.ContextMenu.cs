using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    public enum ContextMenuAction { None, Play, PlayNext, RemoveFromPlaylist }
    private bool _ctxVisible;
    private int _ctxItemIndex = -1;
    private int _ctxX, _ctxY;
    private const int CtxItemH = 32;
    private const int CtxWidth = 200;
    private static readonly string[] CtxLabels = { "Odtwórz", "Odtwórz jako następny", "Usuń z playlisty" };
    private static readonly ContextMenuAction[] CtxActions = { ContextMenuAction.Play, ContextMenuAction.PlayNext, ContextMenuAction.RemoveFromPlaylist };

    public int ContextMenuTargetItem => _ctxItemIndex;
    public bool IsContextMenuVisible => _ctxVisible;

    public void ShowContextMenu(int x, int y, int itemIndex)
    {
        _ctxVisible = true;
        _ctxItemIndex = itemIndex;
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
}
