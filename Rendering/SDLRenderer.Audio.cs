using SDL2;

namespace CSharpFFmpeg;

public sealed partial class SDLRenderer
{
    public void InitAudio(int sampleRate, int channels, Action<byte[]> audioCallback)
    {
        SDL.SDL_InitSubSystem(SDL.SDL_INIT_AUDIO);
        _userAudioCallback = audioCallback;
        _audioCallbackDelegate = (userdata, stream, len) =>
        {
            var buf = new byte[len];
            _userAudioCallback(buf);
            float vol = Math.Clamp(Volume, 0f, 1.5f);
            if (Math.Abs(vol - 1.0f) > 0.005f)
            {
                for (int i = 0; i + 1 < len; i += 2)
                {
                    short s = (short)(buf[i] | (buf[i + 1] << 8));
                    int scaled = (int)(s * vol);
                    scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
                    buf[i]     = (byte)(scaled & 0xFF);
                    buf[i + 1] = (byte)((scaled >> 8) & 0xFF);
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, stream, len);
        };
        var spec = new SDL.SDL_AudioSpec
        {
            freq = sampleRate,
            format = SDL.AUDIO_S16LSB,
            channels = (byte)channels,
            silence = 0,
            samples = 1024,
            callback = _audioCallbackDelegate
        };
        var obtained = new SDL.SDL_AudioSpec();
        if (SDL.SDL_OpenAudio(ref spec, out obtained) < 0)
            throw new InvalidOperationException($"SDL_OpenAudio failed: {SDL.SDL_GetError()}");
        SDL.SDL_PauseAudio(0);
        _audioInitialized = true;
    }

    public void StopAudio()
    {
        if (_audioInitialized)
        {
            SDL.SDL_PauseAudio(1);
            SDL.SDL_CloseAudio();
            _audioInitialized = false;
            Log("Audio stopped");
        }
    }
}
