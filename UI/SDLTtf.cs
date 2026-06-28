using System.Runtime.InteropServices;
using SDL2;

namespace CSharpFFmpeg;

internal static class SDLTtf
{
    private const string Lib = "SDL2_ttf";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TTF_Init();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TTF_OpenFont(string file, int ptsize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TTF_CloseFont(IntPtr font);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TTF_Quit();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TTF_RenderUTF8_Blended(IntPtr font, string text, SDL.SDL_Color color);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TTF_SizeUTF8(IntPtr font, string text, out int w, out int h);
}
