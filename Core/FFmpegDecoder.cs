using FFmpeg.AutoGen;
using System.Runtime.InteropServices;

namespace CSharpFFmpeg;

public sealed unsafe class FFmpegDecoder : IDisposable
{
    [DllImport("libavutil", CallingConvention = CallingConvention.Cdecl)]
    private static extern int av_strerror(int errnum, byte* errbuf, ulong errbuf_size);

    private AVFormatContext* _fmtCtx;
    private AVCodecContext* _vCodecCtx;
    private AVCodecContext* _aCodecCtx;
    private SwsContext* _swsCtx;
    private SwrContext* _swrCtx;
    private AVFrame* _frame;
    private AVFrame* _swFrame;
    private AVPacket* _packet;
    private AVBufferRef* _hwDeviceCtx;
    private bool _useHwAccel;
    private string _currentUrl = "";
    private double _lastSeekPos;

    // Interrupt callback for timeout during open/find_stream_info
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InterruptCallbackDelegate(void* opaque);

    private static readonly InterruptCallbackDelegate _interruptDelegate = InterruptCallback;
    private static long _interruptDeadline;

    private const int OpenTimeoutMs = 15000;

    private static int InterruptCallback(void* opaque)
    {
        return Environment.TickCount64 > _interruptDeadline ? 1 : 0;
    }

    public static string ErrorString(int errorCode)
    {
        var buf = new byte[256];
        fixed (byte* p = buf)
        {
            av_strerror(errorCode, p, (ulong)buf.Length);
        }
        return System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0');
    }

    public bool UseHwAccel { set => _useHwAccel = value; }

    public int VideoStreamIndex { get; private set; } = -1;
    public int AudioStreamIndex { get; private set; } = -1;
    public int VideoWidth => _vCodecCtx != null ? _vCodecCtx->width : 0;
    public int VideoHeight => _vCodecCtx != null ? _vCodecCtx->height : 0;
    public int SampleRate => _aCodecCtx != null ? _aCodecCtx->sample_rate : 0;
    public int Channels => _aCodecCtx != null ? _aCodecCtx->ch_layout.nb_channels : 0;
    public double DurationSec => _fmtCtx != null ? _fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE : 0;
    public double FPS => _vCodecCtx != null && _vCodecCtx->framerate.den > 0 ? _vCodecCtx->framerate.num / (double)_vCodecCtx->framerate.den : 0;

    public void Open(string url)
    {
        // Suppress verbose FFmpeg logging (HLS segment open/close, TLS errors)
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        _currentUrl = url;
        int ret;
        AVDictionary* opts = null;
        bool isNetwork = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (isNetwork)
        {
            ffmpeg.av_dict_set(&opts, "user_agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36", 0);
            ffmpeg.av_dict_set(&opts, "reconnect", "1", 0);
            ffmpeg.av_dict_set(&opts, "reconnect_streamed", "1", 0);
            ffmpeg.av_dict_set(&opts, "reconnect_at_eof", "1", 0);
            ffmpeg.av_dict_set(&opts, "reconnect_delay_max", "5", 0);
            ffmpeg.av_dict_set(&opts, "analyzeduration", "10000000", 0);
            ffmpeg.av_dict_set(&opts, "probesize", "10000000", 0);
            ffmpeg.av_dict_set(&opts, "fflags", "+discardcorrupt", 0);
            ffmpeg.av_dict_set(&opts, "rw_timeout", "15000000", 0);
            ffmpeg.av_dict_set(&opts, "timeout", "15000000", 0);
            ffmpeg.av_dict_set(&opts, "buffer_size", "1048576", 0);
            // VLC-like: fresh connection per segment, no persistent HTTP/TLS
            ffmpeg.av_dict_set(&opts, "http_persistent", "0", 0);
            ffmpeg.av_dict_set(&opts, "http_multiple", "0", 0);
        }

        // Pre-allocate format context and set interrupt callback for timeout
        _fmtCtx = ffmpeg.avformat_alloc_context();
        _interruptDeadline = Environment.TickCount64 + OpenTimeoutMs;
        _fmtCtx->interrupt_callback = new AVIOInterruptCB
        {
            callback = new AVIOInterruptCB_callback_func { Pointer = Marshal.GetFunctionPointerForDelegate(_interruptDelegate) },
            opaque = null
        };

        fixed (AVFormatContext** pFmt = &_fmtCtx)
        {
            ret = ffmpeg.avformat_open_input(pFmt, url, null, &opts);
            if (opts != null) ffmpeg.av_dict_free(&opts);
            if (ret < 0)
            {
                _fmtCtx = null;
                throw new InvalidOperationException($"avformat_open_input failed: {ret} ({ErrorString(ret)})");
            }
        }
        ret = ffmpeg.avformat_find_stream_info(_fmtCtx, null);
        if (ret < 0)
        {
            Dispose();
            throw new InvalidOperationException($"avformat_find_stream_info failed: {ret} ({ErrorString(ret)})");
        }

        // Clear interrupt callback after successful open
        _fmtCtx->interrupt_callback = new AVIOInterruptCB { callback = default, opaque = null };

        // Increase internal packet buffer for smoother HLS segment transitions
        _fmtCtx->max_analyze_duration = 100_000_000;
        _fmtCtx->probesize = 100_000_000;

        for (int i = 0; i < _fmtCtx->nb_streams; i++)
        {
            var st = _fmtCtx->streams[i];
            if (st->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && VideoStreamIndex == -1)
                VideoStreamIndex = i;
            else if (st->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && AudioStreamIndex == -1)
                AudioStreamIndex = i;
        }

        if (VideoStreamIndex >= 0)
        {
            _vCodecCtx = OpenVideoCodec(VideoStreamIndex);
            _swsCtx = ffmpeg.sws_getContext(
                _vCodecCtx->width, _vCodecCtx->height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                _vCodecCtx->width, _vCodecCtx->height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
        }
        if (AudioStreamIndex >= 0) _aCodecCtx = OpenCodec(AudioStreamIndex);

        _frame = ffmpeg.av_frame_alloc();
        _swFrame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        if (_aCodecCtx != null)
        {
            AVChannelLayout outLayout = new();
            ffmpeg.av_channel_layout_default(&outLayout, 2);
            fixed (SwrContext** pSwr = &_swrCtx)
            {
                ret = ffmpeg.swr_alloc_set_opts2(pSwr,
                    &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, _aCodecCtx->sample_rate,
                    &_aCodecCtx->ch_layout, _aCodecCtx->sample_fmt, _aCodecCtx->sample_rate, 0, null);
                if (ret < 0) throw new InvalidOperationException($"swr_alloc_set_opts2 failed: {ret}");
            }
            ffmpeg.swr_init(_swrCtx);
        }
    }

    private AVCodecContext* OpenVideoCodec(int streamIndex)
    {
        var stream = _fmtCtx->streams[streamIndex];
        var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null) throw new InvalidOperationException($"Codec not found for stream {streamIndex}");
        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(ctx, stream->codecpar);

        if (_useHwAccel)
        {
            TryInitHwAccel(ctx);
        }

        ffmpeg.avcodec_open2(ctx, codec, null);
        return ctx;
    }

    private void TryInitHwAccel(AVCodecContext* ctx)
    {
        AVHWDeviceType[] tryTypes = [
            AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
            AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
            AVHWDeviceType.AV_HWDEVICE_TYPE_VDPAU,
        ];

        foreach (var hwType in tryTypes)
        {
            fixed (AVBufferRef** pDev = &_hwDeviceCtx)
            {
                int ret = ffmpeg.av_hwdevice_ctx_create(pDev, hwType, null, null, 0);
                if (ret >= 0)
                {
                    ctx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
                    Console.Error.WriteLine($"[GPU] HW acceleration enabled: {hwType}");
                    return;
                }
            }
        }
        Console.Error.WriteLine("[GPU] No HW acceleration device available, falling back to CPU");
        _useHwAccel = false;
    }

    private AVCodecContext* OpenCodec(int streamIndex)
    {
        var stream = _fmtCtx->streams[streamIndex];
        var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null) throw new InvalidOperationException($"Codec not found for stream {streamIndex}");
        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(ctx, stream->codecpar);
        ffmpeg.avcodec_open2(ctx, codec, null);
        return ctx;
    }

    /// <summary>
    /// Read next packet. Returns false on EOF.
    /// Retries on transient network errors (EAGAIN, EINTR).
    /// </summary>
    public bool ReadPacket(out int streamIndex)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int ret = ffmpeg.av_read_frame(_fmtCtx, _packet);
            if (ret >= 0)
            {
                streamIndex = _packet->stream_index;
                return true;
            }
            // AVERROR_EOF or AVERROR_EXIT — genuine end
            if (ret == ffmpeg.AVERROR_EOF || ret == ffmpeg.AVERROR_EXIT)
                break;
            // Network error — try reconnect after a few retries
            Console.Error.WriteLine($"[Decoder] ReadPacket error ret={ret}, attempt {attempt + 1}/5");
            if (attempt >= 2)
            {
                Console.Error.WriteLine($"[Decoder] Attempting reconnect...");
                Thread.Sleep(1000 * (attempt - 1));
                if (Reconnect())
                {
                    Console.Error.WriteLine($"[Decoder] Reconnect succeeded, retrying read");
                    continue;
                }
                Console.Error.WriteLine($"[Decoder] Reconnect failed");
            }
            Thread.Sleep(500 * (attempt + 1));
        }
        streamIndex = -1;
        return false;
    }

    /// <summary>
    /// Send current packet to the appropriate decoder and receive frames.
    /// Call ReceiveVideoFrame / ReceiveAudioFrame after.
    /// </summary>
    public int SendPacket()
    {
        if (_packet->stream_index == VideoStreamIndex)
            return ffmpeg.avcodec_send_packet(_vCodecCtx, _packet);
        if (_packet->stream_index == AudioStreamIndex)
            return ffmpeg.avcodec_send_packet(_aCodecCtx, _packet);
        return 0;
    }

    public void UnrefPacket() => ffmpeg.av_packet_unref(_packet);
    public void UnrefFrame() => ffmpeg.av_frame_unref(_frame);

    public bool ReceiveVideoFrame()
    {
        int ret = ffmpeg.avcodec_receive_frame(_vCodecCtx, _frame);
        if (ret != 0) return false;

        if (_frame->format < 0 || _frame->hw_frames_ctx != null)
        {
            ffmpeg.av_frame_unref(_swFrame);
            ret = ffmpeg.av_hwframe_transfer_data(_swFrame, _frame, 0);
            if (ret < 0)
            {
                ffmpeg.av_frame_unref(_frame);
                return false;
            }
            _swFrame->pts = _frame->pts;
            ffmpeg.av_frame_unref(_frame);
            ffmpeg.av_frame_move_ref(_frame, _swFrame);
        }
        return true;
    }

    public bool ReceiveAudioFrame()
    {
        int ret = ffmpeg.avcodec_receive_frame(_aCodecCtx, _frame);
        return ret == 0;
    }

    /// <summary>
    /// Convert current video frame to YUV420P and copy into provided buffers.
    /// </summary>
    public void CopyVideoFrame(IntPtr yPlane, IntPtr uPlane, IntPtr vPlane, int yStride, int uvStride)
    {
        AVPixelFormat srcFmt = (AVPixelFormat)_frame->format;
        if (srcFmt != AVPixelFormat.AV_PIX_FMT_YUV420P)
        {
            var tmpSws = ffmpeg.sws_getContext(
                _vCodecCtx->width, _vCodecCtx->height, srcFmt,
                _vCodecCtx->width, _vCodecCtx->height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            var srcSlice = new byte*[] { _frame->data[0], _frame->data[1], _frame->data[2], null, null, null, null, null };
            var srcStride = new int[] { _frame->linesize[0], _frame->linesize[1], _frame->linesize[2], 0, 0, 0, 0, 0 };
            var dst = new byte*[] { (byte*)yPlane, (byte*)uPlane, (byte*)vPlane, null, null, null, null, null };
            var dstStride = new int[] { yStride, uvStride, uvStride, 0, 0, 0, 0, 0 };
            ffmpeg.sws_scale(tmpSws, srcSlice, srcStride, 0, _vCodecCtx->height, dst, dstStride);
            ffmpeg.sws_freeContext(tmpSws);
        }
        else
        {
            var srcSlice = new byte*[] { _frame->data[0], _frame->data[1], _frame->data[2], null, null, null, null, null };
            var srcStride = new int[] { _frame->linesize[0], _frame->linesize[1], _frame->linesize[2], 0, 0, 0, 0, 0 };
            var dst = new byte*[] { (byte*)yPlane, (byte*)uPlane, (byte*)vPlane, null, null, null, null, null };
            var dstStride = new int[] { yStride, uvStride, uvStride, 0, 0, 0, 0, 0 };
            ffmpeg.sws_scale(_swsCtx, srcSlice, srcStride, 0, _vCodecCtx->height, dst, dstStride);
        }
    }

    /// <summary>
    /// Convert current audio frame to S16 stereo and return PCM bytes.
    /// </summary>
    public byte[] CopyAudioFrame()
    {
        int maxOut = ffmpeg.swr_get_out_samples(_swrCtx, _frame->nb_samples) * 2 * 2;
        var buf = new byte[maxOut];
        int converted;
        fixed (byte* pBuf = buf)
        {
            byte* outPtr = pBuf;
            byte* inPtr = _frame->data[0];
            converted = ffmpeg.swr_convert(_swrCtx, &outPtr, _frame->nb_samples, &inPtr, _frame->nb_samples);
        }
        if (converted <= 0) return Array.Empty<byte>();
        Array.Resize(ref buf, converted * 2 * 2);
        return buf;
    }

    public double GetVideoFramePts()
    {
        long pts = _frame->pts;
        if (pts == ffmpeg.AV_NOPTS_VALUE)
            pts = _frame->best_effort_timestamp;
        if (pts == ffmpeg.AV_NOPTS_VALUE)
            pts = _frame->pkt_dts;
        if (pts == ffmpeg.AV_NOPTS_VALUE) return 0;
        var tb = _fmtCtx->streams[VideoStreamIndex]->time_base;
        return pts * tb.num / (double)tb.den;
    }

    public double GetAudioFramePts()
    {
        if (_frame->pts == ffmpeg.AV_NOPTS_VALUE) return 0;
        var tb = _fmtCtx->streams[AudioStreamIndex]->time_base;
        return _frame->pts * tb.num / (double)tb.den;
    }

    /// <summary>
    /// Seek to targetSec and decode one video frame, returning it as RGBA bytes
    /// scaled to thumbW x thumbH. Returns null on failure.
    /// </summary>
    public byte[]? DecodeThumbnailRgba(double targetSec, int thumbW, int thumbH)
    {
        if (_fmtCtx == null || VideoStreamIndex < 0 || _vCodecCtx == null) return null;

        long targetTs = (long)(targetSec / ffmpeg.av_q2d(_fmtCtx->streams[VideoStreamIndex]->time_base));
        int ret = ffmpeg.av_seek_frame(_fmtCtx, VideoStreamIndex, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (ret < 0) return null;
        ffmpeg.avcodec_flush_buffers(_vCodecCtx);

        // Read packets until we get a video frame
        var pkt = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        var sw = ffmpeg.av_frame_alloc();
        byte[]? result = null;

        try
        {
            for (int attempts = 0; attempts < 200 && result == null; attempts++)
            {
                if (ffmpeg.av_read_frame(_fmtCtx, pkt) < 0) break;
                if (pkt->stream_index != VideoStreamIndex) { ffmpeg.av_packet_unref(pkt); continue; }

                ffmpeg.avcodec_send_packet(_vCodecCtx, pkt);
                ffmpeg.av_packet_unref(pkt);

                if (ffmpeg.avcodec_receive_frame(_vCodecCtx, frame) != 0) continue;

                // HW transfer if needed
                AVFrame* src = frame;
                if (frame->hw_frames_ctx != null)
                {
                    if (ffmpeg.av_hwframe_transfer_data(sw, frame, 0) < 0) { ffmpeg.av_frame_unref(frame); continue; }
                    sw->pts = frame->pts;
                    ffmpeg.av_frame_unref(frame);
                    ffmpeg.av_frame_move_ref(frame, sw);
                    src = frame;
                }

                int srcW = _vCodecCtx->width;
                int srcH = _vCodecCtx->height;
                if (srcW <= 0 || srcH <= 0) { ffmpeg.av_frame_unref(frame); continue; }

                var sws = ffmpeg.sws_getContext(
                    srcW, srcH, (AVPixelFormat)src->format,
                    thumbW, thumbH, AVPixelFormat.AV_PIX_FMT_RGB24,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);
                if (sws == null) { ffmpeg.av_frame_unref(frame); break; }

                int stride = thumbW * 3;
                result = new byte[stride * thumbH];
                fixed (byte* pResult = result)
                {
                    var dstSlice  = new byte*[] { pResult, null, null, null, null, null, null, null };
                    var dstStride = new int[]   { stride, 0, 0, 0, 0, 0, 0, 0 };
                    var srcSliceT = new byte*[] { src->data[0], src->data[1], src->data[2], null, null, null, null, null };
                    var srcStrideT= new int[]   { src->linesize[0], src->linesize[1], src->linesize[2], 0, 0, 0, 0, 0 };
                    ffmpeg.sws_scale(sws, srcSliceT, srcStrideT, 0, srcH, dstSlice, dstStride);
                }
                ffmpeg.sws_freeContext(sws);
                ffmpeg.av_frame_unref(frame);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_frame_free(&sw);
        }
        return result;
    }

    public bool Seek(double targetSec)
    {
        _lastSeekPos = targetSec;
        long targetTs = (long)(targetSec / ffmpeg.av_q2d(_fmtCtx->streams[VideoStreamIndex]->time_base));
        int ret = ffmpeg.av_seek_frame(_fmtCtx, VideoStreamIndex, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (ret < 0) return false;
        FlushDecoders();
        return true;
    }

    /// <summary>
    /// Reconnect to the current stream after a network error (e.g. TLS reset).
    /// Disposes and re-opens the format context, then seeks to the last known position.
    /// </summary>
    public bool Reconnect()
    {
        if (string.IsNullOrEmpty(_currentUrl)) return false;
        double pos = _lastSeekPos;
        int savedVideo = VideoStreamIndex;
        int savedAudio = AudioStreamIndex;

        Dispose();
        VideoStreamIndex = savedVideo;
        AudioStreamIndex = savedAudio;

        try
        {
            Open(_currentUrl);
        }
        catch
        {
            return false;
        }

        if (pos > 0)
        {
            Seek(pos);
        }
        return true;
    }

    private void FlushDecoders()
    {
        if (_vCodecCtx != null) ffmpeg.avcodec_flush_buffers(_vCodecCtx);
        if (_aCodecCtx != null) ffmpeg.avcodec_flush_buffers(_aCodecCtx);
    }

    public void Dispose()
    {
        if (_frame != null) { fixed (AVFrame** p = &_frame) ffmpeg.av_frame_free(p); _frame = null; }
        if (_swFrame != null) { fixed (AVFrame** p = &_swFrame) ffmpeg.av_frame_free(p); _swFrame = null; }
        if (_packet != null) { fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p); _packet = null; }
        if (_swsCtx != null) { ffmpeg.sws_freeContext(_swsCtx); _swsCtx = null; }
        if (_swrCtx != null) { fixed (SwrContext** p = &_swrCtx) ffmpeg.swr_free(p); _swrCtx = null; }
        if (_hwDeviceCtx != null) { fixed (AVBufferRef** p = &_hwDeviceCtx) ffmpeg.av_buffer_unref(p); _hwDeviceCtx = null; }
        if (_vCodecCtx != null) { fixed (AVCodecContext** p = &_vCodecCtx) ffmpeg.avcodec_free_context(p); _vCodecCtx = null; }
        if (_aCodecCtx != null) { fixed (AVCodecContext** p = &_aCodecCtx) ffmpeg.avcodec_free_context(p); _aCodecCtx = null; }
        if (_fmtCtx != null) { fixed (AVFormatContext** p = &_fmtCtx) ffmpeg.avformat_close_input(p); _fmtCtx = null; }
        VideoStreamIndex = -1;
        AudioStreamIndex = -1;
    }
}
