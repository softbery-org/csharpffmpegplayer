using System.Runtime.InteropServices;
using SDL2;

namespace CSharpFFmpeg;

public sealed partial class Player
{
    private void DecodeLoop()
    {
        var dec = _decoder;  // Capture current decoder
        int packetCount = 0;
        long startTicks = Environment.TickCount64;
        while (_decodeRunning && ReferenceEquals(_decoder, dec))
        {
            if (_paused) { Thread.Sleep(20); continue; }

            if (_seekRequested)
            {
                _seekRequested = false;
                ClearQueues();
                bool seekOk = dec.Seek(_seekTarget);
                Console.Error.WriteLine($"[Decoder] Seek to {_seekTarget:F1}s: ok={seekOk}");
                _clockStarted = false;
                continue;
            }

            lock (_videoLock)
            {
                if (_videoQueue.Count >= MaxVideoQueue)
                {
                    Monitor.Wait(_videoLock, 10);
                    continue;
                }
            }

            if (!dec.ReadPacket(out int streamIdx))
            {
                Console.Error.WriteLine($"[Decoder] ReadPacket returned false (EOF/error), trackEof set. clockStarted={_clockStarted}");
                if (ReferenceEquals(_decoder, dec))
                {
                    _trackEof = true;
                    _decodeRunning = false;
                }
                break;
            }

            int sendRet = dec.SendPacket();
            if (sendRet < 0)
            {
                Console.Error.WriteLine($"[Decoder] SendPacket error: {sendRet} ({FFmpegDecoder.ErrorString(sendRet)})");
                dec.UnrefPacket();
                continue;
            }
            dec.UnrefPacket();
            packetCount++;
            if (packetCount % 100 == 0)
            {
                long elapsed = Environment.TickCount64 - startTicks;
                Console.Error.WriteLine($"[Decoder] {packetCount} packets in {elapsed}ms (vq={_videoQueue.Count} aq={_audioQueue.Count})");
            }

            if (streamIdx == dec.VideoStreamIndex)
            {
                while (dec.ReceiveVideoFrame())
                {
                    double pts = dec.GetVideoFramePts();
                    if (!_clockStarted)
                    {
                        _clockBasePts = pts;
                        _clock.Restart();
                        _clockStarted = true;
                    }
                    var vf = VideoFrame.FromDecoder(dec, _videoWidth, _videoHeight);
                    var vfWithPts = new VideoFrame(pts, vf.YPlane, vf.UPlane, vf.VPlane, vf.YStride, vf.UVStride);
                    lock (_videoLock)
                    {
                        _videoQueue.Enqueue(vfWithPts);
                        Monitor.Pulse(_videoLock);
                    }
                    dec.UnrefFrame();
                }
            }
            else if (streamIdx == dec.AudioStreamIndex)
            {
                while (dec.ReceiveAudioFrame())
                {
                    _audioPts = dec.GetAudioFramePts();
                    var pcm = dec.CopyAudioFrame();
                    if (pcm.Length > 0)
                    {
                        lock (_audioLock)
                        {
                            int totalBytes = 0;
                            foreach (var c in _audioQueue) totalBytes += c.Length;
                            while (totalBytes + pcm.Length > MaxAudioQueueBytes && _audioQueue.Count > 0)
                            {
                                var old = _audioQueue.Dequeue();
                                totalBytes -= old.Length;
                            }
                            _audioQueue.Enqueue(pcm);
                        }
                    }
                    dec.UnrefFrame();
                }
            }
        }
    }

    private double GetMasterClock()
    {
        if (!_clockStarted) return 0;
        return _clockBasePts + _clock.Elapsed.TotalSeconds;
    }

    private void TryRenderNextFrame()
    {
        if (_decoder.VideoStreamIndex < 0) return;

        VideoFrame? frameToRender = null;
        lock (_videoLock)
        {
            while (_videoQueue.Count > 0)
            {
                var f = _videoQueue.Dequeue();
                double delay = f.Pts - GetMasterClock();

                if (delay > 0.02)
                {
                    _videoQueue.Enqueue(f);
                    break;
                }

                if (frameToRender.HasValue)
                    frameToRender.Value.Dispose();
                frameToRender = f;
            }
        }

        if (frameToRender == null) return;

        _renderer.UpdateVideoFrame(frameToRender.Value.YPlane, frameToRender.Value.UPlane, frameToRender.Value.VPlane,
            frameToRender.Value.YStride, frameToRender.Value.UVStride);
        _lastFrame?.Dispose();
        _lastFrame = frameToRender;
    }

    private void RedrawLastFrame()
    {
        _forceRedraw = false;
        if (_lastFrame == null || _decoder.VideoStreamIndex < 0)
        {
            _renderer.RenderUI();
            return;
        }
        _renderer.UpdateVideoFrame(_lastFrame.Value.YPlane, _lastFrame.Value.UPlane, _lastFrame.Value.VPlane,
            _lastFrame.Value.YStride, _lastFrame.Value.UVStride);
    }

    private void ClearQueues()
    {
        lock (_videoLock)
        {
            while (_videoQueue.Count > 0)
            {
                var f = _videoQueue.Dequeue();
                f.Dispose();
            }
            Monitor.Pulse(_videoLock);
        }
        lock (_audioLock)
        {
            _audioQueue.Clear();
        }
        _audioPartial = null;
        _audioPartialOffset = 0;
    }

    private void OnAudioCallback(byte[] buf)
    {
        int offset = 0;
        while (offset < buf.Length)
        {
            if (_audioPartial != null)
            {
                int copy = Math.Min(_audioPartial.Length - _audioPartialOffset, buf.Length - offset);
                Array.Copy(_audioPartial, _audioPartialOffset, buf, offset, copy);
                offset += copy;
                _audioPartialOffset += copy;
                if (_audioPartialOffset >= _audioPartial.Length)
                {
                    _audioPartial = null;
                    _audioPartialOffset = 0;
                }
                continue;
            }

            byte[]? chunk = null;
            lock (_audioLock)
            {
                if (_audioQueue.Count > 0)
                    chunk = _audioQueue.Dequeue();
            }
            if (chunk == null)
            {
                Array.Clear(buf, offset, buf.Length - offset);
                break;
            }

            int copyLen = Math.Min(chunk.Length, buf.Length - offset);
            Array.Copy(chunk, 0, buf, offset, copyLen);
            offset += copyLen;

            if (copyLen < chunk.Length)
            {
                _audioPartial = chunk;
                _audioPartialOffset = copyLen;
            }
        }
    }

    private void ResyncClock()
    {
        if (!_clockStarted || _paused) return;
        _clockBasePts = _audioPts;
        _clock.Restart();
    }
}
