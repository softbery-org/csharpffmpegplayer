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

        // --- IKONA GŁOŚNIKA ---
        int iconX = sliderX - 92;
        int iconCY = sliderY;
        
        // Kolor ikony: czyste białe (jak na grafice)
        SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, 255);

        // 1. Body (prostokąt po lewej)
        int bodyW = 12;
        int bodyH = 18;
        var body = new SDL.SDL_Rect { x = iconX, y = iconCY - bodyH / 2, w = bodyW, h = bodyH };
        SDL.SDL_RenderFillRect(_renderer, ref body);

        // 2. Cone (trapez łączący body z falami)
        int coneStart = iconX + bodyW;
        int coneL = 24;
        int coneH = 32;
        int coneEnd = coneStart + coneL;
        for (int dy = -coneH / 2; dy <= coneH / 2; dy++)
        {
            int absDy = Math.Abs(dy);
            // Wyliczanie nachylenia trapezoidu
            int leftOffset = absDy <= bodyH / 2
                ? 0
                : (int)(coneL * (absDy - bodyH / 2) / (float)(coneH / 2 - bodyH / 2));
            
            int x = coneStart + leftOffset;
            int w = coneEnd - x;
            if (w < 1) w = 1;
            var line = new SDL.SDL_Rect { x = x, y = iconCY + dy, w = w, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref line);
        }

        // 3. Sound waves (ŁUKI, nie koła)
        bool muted = Volume <= 0.001f;
        int waveX = coneEnd + 6; // Nieco bliżej stożka jak na grafice
        int[] waveRadii = { 10, 18, 26 };
        
        for (int i = 0; i < waveRadii.Length; i++)
        {
            // Stopniowe wygaszanie przezroczystości fal
            byte alpha = muted ? (byte)60 : (byte)(255 - (i * 60));
            SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, alpha);
            
            int r = waveRadii[i];
            // Rysujemy łuk używając sin/cos (od -PI/2 do PI/2 to prawa strona)
            for (double angle = -Math.PI / 2 + 0.1; angle < Math.PI / 2 - 0.1; angle += 0.05)
            {
                int dx = (int)(Math.Cos(angle) * r);
                int dy = (int)(Math.Sin(angle) * r);
                SDL.SDL_RenderDrawPoint(_renderer, waveX + dx, iconCY + dy);
            }
        }

        // --- SEGMENTOWANY PASEK GŁOŚNOŚCI ---
        int baseY = sliderY + VolSliderH / 2;
        for (int i = 0; i < VolBarCount; i++)
        {
            float t = (float)i / (VolBarCount - 1);
            // Wysokość rośnie od lewej do prawej (zgodnie z Twoją logiką i obrazkiem)
            int barH = (int)(VolSliderH * (0.35f + 0.65f * t));
            int bx = sliderX + i * (VolBarW + VolBarGap);
            int by = baseY - barH;
            bool filled = i < filledBars;

            if (filled)
                SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, 255); // Białe wypełnienie
            else
                SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 60, 180);    // Ciemnoszary nieaktywny

            var bar = new SDL.SDL_Rect { x = bx, y = by, w = VolBarW, h = barH };
            SDL.SDL_RenderFillRect(_renderer, ref bar);
        }

        // --- PROCENTY ---
        if (_font != IntPtr.Zero)
        {
            string label = $"{(int)(Volume * 100)}%";
            var col = new SDL.SDL_Color { r = 255, g = 255, b = 255, a = 220 };
            IntPtr surf = SDLTtf.TTF_RenderUTF8_Blended(_font, label, col);
            if (surf != IntPtr.Zero)
            {
                IntPtr tex = SDL.SDL_CreateTextureFromSurface(_renderer, surf);
                SDL.SDL_FreeSurface(surf);
                if (tex != IntPtr.Zero)
                {
                    SDL.SDL_QueryTexture(tex, out _, out _, out int tw, out int th);
                    var dst = new SDL.SDL_Rect { x = sliderX + VolSliderW + 12, y = sliderY - th / 2, w = tw, h = th };
                    SDL.SDL_RenderCopy(_renderer, tex, IntPtr.Zero, ref dst);
                    SDL.SDL_DestroyTexture(tex);
                }
            }
        }

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
    }
}
