using SDL2;

namespace CSharpFFmpeg;

public sealed partial class Player
{
    private void EnsureThumbWorkerRunning()
    {
        if (_thumbThread != null && _thumbThread.IsAlive) return;
        _thumbThread = new Thread(ThumbnailWorkerLoop) { IsBackground = true, Name = "Thumb" };
        _thumbThread.Start();
    }

    private void UpdateProgressBarHover(int x, int y)
    {
        if (_duration <= 0 || _videoPath == null) { _renderer.ProgressHoverTime = -1; return; }
        int barY = _renderer.GetProgressBarY();
        int margin = _renderer.PlaylistPanelVisible ? SDLRenderer.PlaylistPanelWidth + 20 : 20;
        _renderer.GetWindowGeometry(out _, out _, out int winW, out _);
        int barW = winW - margin - 20;
        bool onTrack = y >= barY && y <= _renderer.GetWindowHeight() && x >= margin && x <= margin + barW;
        if (!onTrack) { _renderer.ProgressHoverTime = -1; return; }

        double hoverTime = Math.Clamp((x - margin) / (double)barW * _duration, 0, _duration);
        _renderer.ProgressHoverTime = hoverTime;
        _forceRedraw = true;

        Interlocked.Exchange(ref _thumbRequestBits, BitConverter.DoubleToInt64Bits(hoverTime));
        EnsureThumbWorkerRunning();
        _thumbSignal.Set();
    }

    private void ThumbnailWorkerLoop()
    {
        while (_running)
        {
            _thumbSignal.Wait();
            _thumbSignal.Reset();

            Thread.Sleep(80);
            double requestedTime = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _thumbRequestBits));
            if (double.IsNaN(requestedTime) || !_running) continue;

            string? path = _videoPath;
            if (path == null || _duration <= 0) continue;

            try
            {
                using var dec = new FFmpegDecoder();
                dec.Open(path);
                if (dec.VideoStreamIndex < 0) continue;

                double decodeTime = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _thumbRequestBits));
                if (double.IsNaN(decodeTime)) continue;

                var rgb = dec.DecodeThumbnailRgba(decodeTime, SDLRenderer.ThumbW, SDLRenderer.ThumbH);
                if (rgb != null && _running)
                {
                    _renderer.SetThumbnail(rgb, decodeTime);
                    _forceRedraw = true;
                }
            }
            catch { }
        }
    }
}
