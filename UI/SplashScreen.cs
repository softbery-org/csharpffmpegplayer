using System.Runtime.InteropServices;
using SDL2;

namespace CSharpFFmpeg;

internal sealed class SplashScreen : IDisposable
{
    private IntPtr _window;
    private IntPtr _renderer;
    private IntPtr _fontTitle;
    private IntPtr _fontSub;
    private bool _ttfInit;
    private const int Width = 640;
    private const int Height = 380;
    private const int DurationMs = 4500;
    private const int PanelPad = 24;
    private const int PanelRadius = 16;

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_SetWindowOpacity(IntPtr window, float opacity);

    // Logo geometry
    private const int LogoCx = Width / 2;
    private const int LogoCy = 120;
    private const int StripW = 160;
    private const int StripH = 100;
    private const int SprocketSize = 6;
    private const int SprocketSpacing = 14;
    private const int WaveBars = 7;
    private const int WaveBarW = 6;
    private const int WaveBarGap = 4;
    private const int WaveMaxH = 28;

    private static readonly string[] FontPaths = [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/dejavu/DejaVuSans.ttf",
    ];

    // Wave bar heights (symmetric pattern, normalized 0..1)
    private static readonly float[] WavePattern = [0.3f, 0.6f, 1.0f, 0.7f, 1.0f, 0.5f, 0.25f];

    public void Show()
    {
        SDL.SDL_Init(SDL.SDL_INIT_VIDEO);
        SDL.SDL_SetHint(SDL.SDL_HINT_RENDER_SCALE_QUALITY, "1");

        _window = SDL.SDL_CreateWindow(
            "CSharp FFmpeg Player",
            SDL.SDL_WINDOWPOS_CENTERED, SDL.SDL_WINDOWPOS_CENTERED,
            Width, Height,
            SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN | SDL.SDL_WindowFlags.SDL_WINDOW_BORDERLESS);

        _renderer = SDL.SDL_CreateRenderer(_window, -1,
            SDL.SDL_RendererFlags.SDL_RENDERER_ACCELERATED | SDL.SDL_RendererFlags.SDL_RENDERER_PRESENTVSYNC);

        // Enable blend mode for transparency
        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        InitFont();

        long startTicks = SDL.SDL_GetTicks();
        long elapsed = 0;

        while (elapsed < DurationMs)
        {
            elapsed = SDL.SDL_GetTicks() - startTicks;

            while (SDL.SDL_PollEvent(out var ev) != 0)
            {
                if (ev.type == SDL.SDL_EventType.SDL_QUIT ||
                    (ev.type == SDL.SDL_EventType.SDL_KEYDOWN &&
                     ev.key.keysym.sym == SDL.SDL_Keycode.SDLK_ESCAPE))
                {
                    return;
                }
            }

            float t = (float)elapsed / DurationMs;
            t = Math.Clamp(t, 0f, 1f);

            // Clear with full transparency — no background panel
            SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 0);
            SDL.SDL_RenderClear(_renderer);

            // Logo fade-in (first 35% of animation)
            float logoAlpha = EaseOutCubic(Math.Clamp(t * 2.8f, 0f, 1f));
            DrawLogo(logoAlpha, t);

            // Title fade-in
            float titleAlpha = EaseOutCubic(Math.Clamp((t - 0.12f) * 2.2f, 0f, 1f));
            RenderText("CSharp FFmpeg Player", Width / 2, 250, titleAlpha, _fontTitle);

            // Subtitle slide-up + fade
            float subT = Math.Clamp((t - 0.3f) / 0.5f, 0f, 1f);
            float subAlpha = EaseOutCubic(subT);
            int subY = 290 + (int)((1f - subT) * 15);
            RenderText("video & audio player", Width / 2, subY, subAlpha * 0.7f, _fontSub);

            // Credit typewriter + slide-up (delayed)
            const string creditFull = "Softbery by Paweł Tobis";
            float creditStart = 0.4f;
            float creditDur = 0.4f;
            float creditT = Math.Clamp((t - creditStart) / creditDur, 0f, 1f);
            float creditAlpha = EaseOutCubic(Math.Clamp(creditT * 1.5f, 0f, 1f));
            int creditY = (Height - 50) + (int)((1f - EaseOutCubic(creditT)) * 12);
            int creditChars = (int)MathF.Ceiling(creditFull.Length * EaseOutCubic(creditT));
            string creditText = creditFull.Substring(0, Math.Min(creditChars, creditFull.Length));
            bool showCursor = (int)(SDL.SDL_GetTicks() / 400) % 2 == 0 && creditT < 1f;
            if (showCursor) creditText += "_";
            RenderText(creditText, Width / 2, creditY, creditAlpha * 0.55f, _fontSub);

            // Progress bar
            float barT = Math.Clamp((t - 0.15f) / 0.85f, 0f, 1f);
            int barW = (int)(300 * EaseOutCubic(barT));
            int barX = (Width - 300) / 2;
            int barY = Height - 28;
            var barBg = new SDL.SDL_Rect { x = barX, y = barY, w = 300, h = 3 };
            SDL.SDL_SetRenderDrawColor(_renderer, 30, 50, 80, 200);
            SDL.SDL_RenderFillRect(_renderer, ref barBg);
            var barFg = new SDL.SDL_Rect { x = barX, y = barY, w = barW, h = 3 };
            SDL.SDL_SetRenderDrawColor(_renderer, 70, 130, 210, 255);
            SDL.SDL_RenderFillRect(_renderer, ref barFg);

            SDL.SDL_RenderPresent(_renderer);
        }

        // Fade out
        long fadeStart = SDL.SDL_GetTicks();
        while (true)
        {
            long fadeElapsed = SDL.SDL_GetTicks() - fadeStart;
            float fadeT = (float)fadeElapsed / 500f;
            if (fadeT >= 1f) break;

            while (SDL.SDL_PollEvent(out var ev) != 0)
            {
                if (ev.type == SDL.SDL_EventType.SDL_QUIT) return;
            }

            float a = 1f - fadeT;

            SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 0);
            SDL.SDL_RenderClear(_renderer);

            DrawLogo(a, 1f);
            RenderText("CSharp FFmpeg Player", Width / 2, 250, a, _fontTitle);
            RenderText("video & audio player", Width / 2, 290, a * 0.7f, _fontSub);
            RenderText("Softbery by Paweł Tobis", Width / 2, Height - 50, a * 0.55f, _fontSub);

            SDL.SDL_RenderPresent(_renderer);
        }
    }

    // ── Rounded panel background ──────────────────────────────────────────────

    private void DrawRoundedPanel(int x, int y, int w, int h, int radius, byte r, byte g, byte b, byte a)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, r, g, b, a);

        // Fill center rects (avoiding corners)
        var top = new SDL.SDL_Rect { x = x + radius, y = y, w = w - 2 * radius, h = radius };
        SDL.SDL_RenderFillRect(_renderer, ref top);
        var mid = new SDL.SDL_Rect { x = x, y = y + radius, w = w, h = h - 2 * radius };
        SDL.SDL_RenderFillRect(_renderer, ref mid);
        var bot = new SDL.SDL_Rect { x = x + radius, y = y + h - radius, w = w - 2 * radius, h = radius };
        SDL.SDL_RenderFillRect(_renderer, ref bot);

        // Fill corner circles
        FillCircle(x + radius, y + radius, radius);
        FillCircle(x + w - radius - 1, y + radius, radius);
        FillCircle(x + radius, y + h - radius - 1, radius);
        FillCircle(x + w - radius - 1, y + h - radius - 1, radius);
    }

    private void FillCircle(int cx, int cy, int r)
    {
        for (int dy = -r; dy <= r; dy++)
        {
            int dx = (int)Math.Sqrt(r * r - dy * dy);
            var line = new SDL.SDL_Rect { x = cx - dx, y = cy + dy, w = dx * 2, h = 1 };
            SDL.SDL_RenderFillRect(_renderer, ref line);
        }
    }

    // ── Logo: film strip + play triangle + audio wave bars ───────────────────

    private void DrawLogo(float alpha, float animT)
    {
        int sx = LogoCx - StripW / 2;
        int sy = LogoCy - StripH / 2;

        // Film strip body
        var stripRect = new SDL.SDL_Rect { x = sx, y = sy, w = StripW, h = StripH };
        SDL.SDL_SetRenderDrawColor(_renderer, (byte)(45 * alpha), (byte)(50 * alpha), (byte)(60 * alpha), 240);
        SDL.SDL_RenderFillRect(_renderer, ref stripRect);

        // Film strip border (subtle highlight)
        SDL.SDL_SetRenderDrawColor(_renderer, (byte)(70 * alpha), (byte)(80 * alpha), (byte)(100 * alpha), 255);
        SDL.SDL_RenderDrawRect(_renderer, ref stripRect);

        // Sprocket holes (top & bottom rows)
        int holeCount = (StripW - SprocketSpacing) / SprocketSpacing;
        int holeStartX = sx + (StripW - holeCount * SprocketSpacing) / 2;

        for (int i = 0; i < holeCount; i++)
        {
            int hx = holeStartX + i * SprocketSpacing;
            var topHole = new SDL.SDL_Rect { x = hx, y = sy + 5, w = SprocketSize, h = SprocketSize };
            SDL.SDL_SetRenderDrawColor(_renderer, (byte)(6 * alpha), (byte)(10 * alpha), (byte)(22 * alpha), 255);
            SDL.SDL_RenderFillRect(_renderer, ref topHole);
            var botHole = new SDL.SDL_Rect { x = hx, y = sy + StripH - 5 - SprocketSize, w = SprocketSize, h = SprocketSize };
            SDL.SDL_RenderFillRect(_renderer, ref botHole);
        }

        // Inner frame (viewport area between sprockets)
        int innerY = sy + 16;
        int innerH = StripH - 32;
        var innerRect = new SDL.SDL_Rect { x = sx + 4, y = innerY, w = StripW - 8, h = innerH };
        SDL.SDL_SetRenderDrawColor(_renderer, (byte)(20 * alpha), (byte)(28 * alpha), (byte)(44 * alpha), 255);
        SDL.SDL_RenderFillRect(_renderer, ref innerRect);

        // Play triangle (centered in inner frame)
        float triAlpha = EaseOutCubic(Math.Clamp(animT * 3f, 0f, 1f));
        int triCx = LogoCx;
        int triCy = innerY + innerH / 2;
        int triSize = (int)(28 * EaseOutBack(Math.Clamp(animT * 2.5f, 0f, 1f)));
        DrawPlayTriangle(triCx, triCy, triSize, triAlpha * alpha);

        // Audio wave bars (below film strip)
        float waveT = Math.Clamp((animT - 0.2f) / 0.6f, 0f, 1f);
        int waveY = sy + StripH + 12;
        int totalWaveW = WaveBars * WaveBarW + (WaveBars - 1) * WaveBarGap;
        int waveStartX = LogoCx - totalWaveW / 2;

        for (int i = 0; i < WaveBars; i++)
        {
            float barDelay = i * 0.08f;
            float barAnim = Math.Clamp((waveT - barDelay) / (1f - barDelay), 0f, 1f);
            float barEase = EaseOutCubic(barAnim);
            float pattern = WavePattern[i % WavePattern.Length];
            int barH = (int)(WaveMaxH * pattern * barEase * alpha);
            if (barH < 1) barH = 1;

            int bx = waveStartX + i * (WaveBarW + WaveBarGap);
            var barRect = new SDL.SDL_Rect { x = bx, y = waveY + (WaveMaxH - barH), w = WaveBarW, h = barH };

            float frac = (float)i / (WaveBars - 1);
            SDL.SDL_SetRenderDrawColor(_renderer,
                (byte)((60 + 40 * (1 - frac)) * alpha),
                (byte)((140 + 60 * (1 - frac)) * alpha),
                (byte)((220 + 35 * frac) * alpha), 255);
            SDL.SDL_RenderFillRect(_renderer, ref barRect);
        }
    }

    private void DrawPlayTriangle(int cx, int cy, int size, float alpha)
    {
        if (size < 2) return;

        SDL.SDL_SetRenderDrawColor(_renderer, (byte)(100 * alpha), (byte)(180 * alpha), (byte)(255 * alpha), 255);

        int x0 = cx - size / 3;
        int x1 = cx + size / 2;
        int yTop = cy - size / 2;
        int yBot = cy + size / 2;

        for (int y = yTop; y <= yBot; y++)
        {
            float frac = (float)(y - yTop) / (yBot - yTop);
            int leftX = x0;
            int rightX = x0 + (int)((x1 - x0) * (1f - Math.Abs(2f * frac - 1f)));
            SDL.SDL_RenderDrawLine(_renderer, leftX, y, rightX, y);
        }
    }

    // ── Font & text rendering ─────────────────────────────────────────────────

    private void InitFont()
    {
        if (SDLTtf.TTF_Init() < 0) return;
        _ttfInit = true;
        foreach (var path in FontPaths)
        {
            if (File.Exists(path))
            {
                _fontTitle = SDLTtf.TTF_OpenFont(path, 32);
                if (_fontTitle != IntPtr.Zero)
                {
                    _fontSub = SDLTtf.TTF_OpenFont(path, 15);
                    return;
                }
            }
        }
    }

    private void RenderText(string text, int cx, int cy, float alpha, IntPtr font)
    {
        if (font == IntPtr.Zero) return;

        var color = new SDL.SDL_Color
        {
            r = (byte)(220 * alpha),
            g = (byte)(230 * alpha),
            b = (byte)(255 * alpha),
            a = (byte)(255 * alpha),
        };

        IntPtr surface = SDLTtf.TTF_RenderUTF8_Blended(font, text, color);
        if (surface == IntPtr.Zero) return;

        IntPtr texture = SDL.SDL_CreateTextureFromSurface(_renderer, surface);
        SDL.SDL_FreeSurface(surface);
        if (texture == IntPtr.Zero) return;

        SDL.SDL_QueryTexture(texture, out _, out _, out int w, out int h);
        var dst = new SDL.SDL_Rect { x = cx - w / 2, y = cy - h / 2, w = w, h = h };
        SDL.SDL_RenderCopy(_renderer, texture, IntPtr.Zero, ref dst);
        SDL.SDL_DestroyTexture(texture);
    }

    // ── Easing functions ──────────────────────────────────────────────────────

    private static float EaseOutCubic(float t) => 1f - MathF.Pow(1f - t, 3f);

    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return 1f + c3 * MathF.Pow(t - 1f, 3f) + c1 * MathF.Pow(t - 1f, 2f);
    }

    public void Dispose()
    {
        if (_fontTitle != IntPtr.Zero)
        {
            SDLTtf.TTF_CloseFont(_fontTitle);
            _fontTitle = IntPtr.Zero;
        }
        if (_fontSub != IntPtr.Zero)
        {
            SDLTtf.TTF_CloseFont(_fontSub);
            _fontSub = IntPtr.Zero;
        }
        if (_renderer != IntPtr.Zero)
        {
            SDL.SDL_DestroyRenderer(_renderer);
            _renderer = IntPtr.Zero;
        }
        if (_window != IntPtr.Zero)
        {
            SDL.SDL_DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
        if (_ttfInit)
        {
            SDLTtf.TTF_Quit();
            _ttfInit = false;
        }
        SDL.SDL_Quit();
    }
}
