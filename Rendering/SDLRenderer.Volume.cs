using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    private const float VolMax   = 1.5f;

    public void ShowVolumeHud() { }

    public float HitTestVolumeBar(int mx, int my)
    {
        if (!ControlsVisible) return -1f;
        (int sliderX, int sliderY) = GetVolumeBarPos();
        if (mx < sliderX || mx > sliderX + VolSliderW) return -1f;
        if (my < sliderY - 12 || my > sliderY + 12) return -1f;
        float frac = (mx - sliderX) / (float)VolSliderW;
        return Math.Clamp(frac * VolMax, 0f, VolMax);
    }

    public float SampleVolumeBarX(int mx)
    {
        if (!ControlsVisible) return -1f;
        (int sliderX, int sliderY) = GetVolumeBarPos();
        float frac = (mx - sliderX) / (float)VolSliderW;
        return Math.Clamp(frac * VolMax, 0f, VolMax);
    }

    private (int x, int y) GetVolumeBarPos()
    {
        int sliderX = (_windowW - VolSliderW) / 2;
        int sliderY = _windowH / 2 + 72;
        return (sliderX, sliderY);
    }

    private void DrawVolumeHud()
    {
        if (!ControlsVisible) return;

        (int sliderX, int sliderY) = GetVolumeBarPos();
        float fillFrac = Math.Clamp(Volume / VolMax, 0f, 1f);
        int filledBars = (int)(VolBarCount * fillFrac);
        if (fillFrac > 0 && filledBars == 0) filledBars = 1;

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        // Speaker icon
        SDL.SDL_SetRenderDrawColor(_renderer, 200, 200, 200, 200);
        int iconX = sliderX - 24;
        int iconCY = sliderY;
        var spkBody = new SDL.SDL_Rect { x = iconX, y = iconCY - 3, w = 4, h = 6 };
        SDL.SDL_RenderFillRect(_renderer, ref spkBody);
        for (int dy = -6; dy <= 6; dy++)
        {
            int halfW = 3 + (int)(3.0 * (1.0 - (double)Math.Abs(dy) / 6));
            var r = new SDL.SDL_Rect { x = iconX + 4, y = iconCY + dy, w = halfW, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref r);
        }
        bool muted = Volume <= 0.001f;
        byte waveAlpha = muted ? (byte)80 : (byte)180;
        SDL.SDL_SetRenderDrawColor(_renderer, 200, 200, 200, waveAlpha);
        for (int dy = -4; dy <= 4; dy++)
        {
            int dx = (int)Math.Sqrt(16 - dy * dy);
            SDL.SDL_RenderDrawPoint(_renderer, iconX + 11 + dx, iconCY + dy);
        }
        if (!muted)
        {
            for (int dy = -7; dy <= 7; dy++)
            {
                int dx = (int)Math.Sqrt(49 - dy * dy);
                if (dx > 4)
                    SDL.SDL_RenderDrawPoint(_renderer, iconX + 11 + dx, iconCY + dy);
            }
        }

        // Segmented volume bar (audio wave style)
        int baseY = sliderY + VolSliderH / 2;
        int startX = sliderX;
        for (int i = 0; i < VolBarCount; i++)
        {
            float t = (float)i / (VolBarCount - 1);
            int barH = (int)(VolSliderH * (0.35f + 0.65f * t));
            int bx = startX + i * (VolBarW + VolBarGap);
            int by = baseY - barH;
            bool filled = i < filledBars;
            SDL.SDL_SetRenderDrawColor(_renderer,
                filled ? (byte)220 : (byte)80,
                filled ? (byte)220 : (byte)80,
                filled ? (byte)220 : (byte)80,
                filled ? (byte)220 : (byte)120);
            var r = new SDL.SDL_Rect { x = bx, y = by, w = VolBarW, h = barH };
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
}
