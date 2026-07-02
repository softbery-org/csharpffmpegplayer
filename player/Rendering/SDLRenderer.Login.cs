using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    public bool LoginVisible = false;
    public string LoginServerUrl = "";
    public string LoginUsername = "";
    public string LoginPassword = "";
    public int LoginFieldFocus = 0; // 0=server, 1=user, 2=pass
    public string LoginError = "";
    public bool LoginInProgress = false;

    private SDL.SDL_Rect _loginBox;
    private SDL.SDL_Rect _loginServerField;
    private SDL.SDL_Rect _loginUserField;
    private SDL.SDL_Rect _loginPassField;
    private SDL.SDL_Rect _loginBtnLogin;
    private SDL.SDL_Rect _loginBtnCancel;

    public enum LoginHit { None, ServerField, UserField, PassField, LoginBtn, CancelBtn }

    public LoginHit HitTestLogin(int x, int y)
    {
        if (!LoginVisible) return LoginHit.None;
        if (Contains(_loginServerField, x, y)) return LoginHit.ServerField;
        if (Contains(_loginUserField, x, y)) return LoginHit.UserField;
        if (Contains(_loginPassField, x, y)) return LoginHit.PassField;
        if (Contains(_loginBtnLogin, x, y)) return LoginHit.LoginBtn;
        if (Contains(_loginBtnCancel, x, y)) return LoginHit.CancelBtn;
        return LoginHit.None;
    }

    private void DrawLogin()
    {
        if (_font == IntPtr.Zero) return;

        int panW = 460, panH = 320;
        int panX = (_windowW - panW) / 2;
        int panY = (_windowH - panH) / 2;

        _loginBox = new SDL.SDL_Rect { x = panX, y = panY, w = panW, h = panH };

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

        // Dim background
        SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 160);
        var fullScreen = new SDL.SDL_Rect { x = 0, y = 0, w = _windowW, h = _windowH };
        SDL.SDL_RenderFillRect(_renderer, ref fullScreen);

        // Panel shadow
        SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 100);
        var shadow = new SDL.SDL_Rect { x = panX + 4, y = panY + 4, w = panW, h = panH };
        SDL.SDL_RenderFillRect(_renderer, ref shadow);

        // Panel background
        SDL.SDL_SetRenderDrawColor(_renderer, 28, 30, 36, 250);
        SDL.SDL_RenderFillRect(_renderer, ref _loginBox);
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 255, 220);
        SDL.SDL_RenderDrawRect(_renderer, ref _loginBox);

        SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);

        var white = new SDL.SDL_Color { r = 230, g = 230, b = 230, a = 255 };
        var blue = new SDL.SDL_Color { r = 80, g = 160, b = 255, a = 255 };
        var gray = new SDL.SDL_Color { r = 150, g = 150, b = 150, a = 255 };
        var muted = new SDL.SDL_Color { r = 110, g = 110, b = 110, a = 255 };
        var red = new SDL.SDL_Color { r = 255, g = 100, b = 100, a = 255 };

        int y = panY + 24;

        // Title
        RenderText("Logowanie do serwera", panX + 24, y, blue);
        y += 36;

        // Server URL field
        RenderText("Adres serwera", panX + 24, y, gray);
        y += 22;
        int fieldW = panW - 48;
        int fieldH = 30;
        _loginServerField = new SDL.SDL_Rect { x = panX + 24, y = y, w = fieldW, h = fieldH };
        DrawLoginField(_loginServerField, LoginServerUrl, LoginFieldFocus == 0, false);
        y += fieldH + 12;

        // Username field
        RenderText("Nazwa użytkownika", panX + 24, y, gray);
        y += 22;
        _loginUserField = new SDL.SDL_Rect { x = panX + 24, y = y, w = fieldW, h = fieldH };
        DrawLoginField(_loginUserField, LoginUsername, LoginFieldFocus == 1, false);
        y += fieldH + 12;

        // Password field
        RenderText("Hasło", panX + 24, y, gray);
        y += 22;
        _loginPassField = new SDL.SDL_Rect { x = panX + 24, y = y, w = fieldW, h = fieldH };
        DrawLoginField(_loginPassField, LoginPassword, LoginFieldFocus == 2, true);
        y += fieldH + 16;

        // Error message
        if (!string.IsNullOrEmpty(LoginError))
        {
            RenderText(LoginError, panX + 24, y, red);
        }
        else if (LoginInProgress)
        {
            RenderText("Logowanie...", panX + 24, y, blue);
        }

        // Buttons
        int btnY = panY + panH - 50;
        int btnW = 100, btnH = 32;
        _loginBtnLogin = new SDL.SDL_Rect { x = panX + panW - 240, y = btnY, w = btnW, h = btnH };
        _loginBtnCancel = new SDL.SDL_Rect { x = panX + panW - 128, y = btnY, w = btnW, h = btnH };

        // Login button
        SDL.SDL_SetRenderDrawColor(_renderer, 40, 80, 50, 220);
        SDL.SDL_RenderFillRect(_renderer, ref _loginBtnLogin);
        SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 90, 240);
        SDL.SDL_RenderDrawRect(_renderer, ref _loginBtnLogin);
        RenderText("Zaloguj", _loginBtnLogin.x + 20, _loginBtnLogin.y + 7,
            new SDL.SDL_Color { r = 140, g = 230, b = 140, a = 255 });

        // Cancel button
        SDL.SDL_SetRenderDrawColor(_renderer, 60, 40, 40, 220);
        SDL.SDL_RenderFillRect(_renderer, ref _loginBtnCancel);
        SDL.SDL_SetRenderDrawColor(_renderer, 140, 80, 80, 240);
        SDL.SDL_RenderDrawRect(_renderer, ref _loginBtnCancel);
        RenderText("Anuluj", _loginBtnCancel.x + 22, _loginBtnCancel.y + 7,
            new SDL.SDL_Color { r = 230, g = 160, b = 160, a = 255 });

        // Hint
        var hint = new SDL.SDL_Color { r = 100, g = 100, b = 100, a = 255 };
        RenderText("TAB — następne pole  |  ENTER — zaloguj  |  ESC — anuluj",
            panX + 24, panY + panH - 22, hint);
    }

    private void DrawLoginField(SDL.SDL_Rect rect, string text, bool focused, bool password)
    {
        SDL.SDL_SetRenderDrawColor(_renderer, 20, 22, 28, 240);
        SDL.SDL_RenderFillRect(_renderer, ref rect);

        if (focused)
        {
            SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 255, 255);
        }
        else
        {
            SDL.SDL_SetRenderDrawColor(_renderer, 60, 60, 70, 200);
        }
        SDL.SDL_RenderDrawRect(_renderer, ref rect);

        string display = password ? new string('*', text.Length) : text;
        if (!string.IsNullOrEmpty(display))
        {
            RenderText(display, rect.x + 8, rect.y + 6,
                new SDL.SDL_Color { r = 230, g = 230, b = 230, a = 255 });
        }
        else if (focused)
        {
            // Cursor placeholder
            var cur = new SDL.SDL_Rect { x = rect.x + 8, y = rect.y + 6, w = 2, h = 18 };
            SDL.SDL_SetRenderDrawColor(_renderer, 80, 160, 255, 255);
            SDL.SDL_RenderFillRect(_renderer, ref cur);
        }
    }
}
