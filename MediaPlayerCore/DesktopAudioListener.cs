using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;

namespace MediaPlayerCore;

/// <summary>
/// Captures the default Windows playback device mix (WASAPI loopback) for audio-reactive visuals.
/// </summary>
public sealed class DesktopAudioListener : IDisposable
{
    private readonly MediaManager _manager;
    private WasapiLoopbackCapture? _capture;
    private byte[] _monoBuffer = Array.Empty<byte>();
    private byte[] _convertBuffer = Array.Empty<byte>();
    private int _batchBytes;
    private bool _disposed;

    private const int BatchThreshold = 4096;

    public DesktopAudioListener(MediaManager manager)
    {
        _manager = manager;
    }

    public bool IsRunning => _capture != null;

    public void Start()
    {
        if (_disposed || _capture != null)
        {
            return;
        }

        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DesktopAudioListener] Failed to start: {ex.Message}");
            _capture?.Dispose();
            _capture = null;
            _manager.RaiseError(ex);
        }
    }

    public void Stop()
    {
        if (_capture == null)
        {
            return;
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try
        {
            _capture.StopRecording();
        }
        catch
        {
            // Device may already be gone during shutdown.
        }

        _capture.Dispose();
        _capture = null;
        _batchBytes = 0;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null && !_disposed)
        {
            Debug.WriteLine($"[DesktopAudioListener] Recording stopped: {e.Exception.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed || _capture == null || e.BytesRecorded <= 0)
        {
            return;
        }

        int monoBytes = ConvertToMonoPcm16(e.Buffer.AsSpan(0, e.BytesRecorded), _capture.WaveFormat);
        if (monoBytes <= 0)
        {
            return;
        }

        AppendMonoPcm16(_convertBuffer.AsSpan(0, monoBytes));
    }

    private int ConvertToMonoPcm16(ReadOnlySpan<byte> input, WaveFormat waveFormat)
    {
        int channels = waveFormat.Channels;
        if (channels <= 0)
        {
            return 0;
        }

        int bytesPerSample = waveFormat.BitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            return 0;
        }

        int frameSize = bytesPerSample * channels;
        int frameCount = input.Length / frameSize;
        if (frameCount <= 0)
        {
            return 0;
        }

        int requiredBytes = frameCount * 2;
        if (_convertBuffer.Length < requiredBytes)
        {
            _convertBuffer = new byte[Math.Max(requiredBytes, _convertBuffer.Length * 2 + 4096)];
        }

        int outOffset = 0;
        if (waveFormat.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            for (int i = 0; i < frameCount; i++)
            {
                float sum = 0f;
                int frameStart = i * frameSize;
                for (int c = 0; c < channels; c++)
                {
                    sum += BitConverter.ToSingle(input.Slice(frameStart + c * 4, 4));
                }

                float mono = Math.Clamp(sum / channels, -1f, 1f);
                short sample = (short)(mono * 32767f);
                _convertBuffer[outOffset++] = (byte)(sample & 0xFF);
                _convertBuffer[outOffset++] = (byte)((sample >> 8) & 0xFF);
            }
        }
        else if (waveFormat.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 2)
        {
            for (int i = 0; i < frameCount; i++)
            {
                int sum = 0;
                int frameStart = i * frameSize;
                for (int c = 0; c < channels; c++)
                {
                    sum += BitConverter.ToInt16(input.Slice(frameStart + c * 2, 2));
                }

                short sample = (short)(sum / channels);
                _convertBuffer[outOffset++] = (byte)(sample & 0xFF);
                _convertBuffer[outOffset++] = (byte)((sample >> 8) & 0xFF);
            }
        }
        else
        {
            return 0;
        }

        return outOffset;
    }

    private void AppendMonoPcm16(ReadOnlySpan<byte> monoPcm)
    {
        int needed = _batchBytes + monoPcm.Length;
        if (_monoBuffer.Length < needed)
        {
            _monoBuffer = new byte[Math.Max(needed, _monoBuffer.Length * 2 + BatchThreshold)];
        }

        monoPcm.CopyTo(_monoBuffer.AsSpan(_batchBytes));
        _batchBytes += monoPcm.Length;

        while (_batchBytes >= BatchThreshold)
        {
            _manager.UpdateAudioVisualsFromDesktop(_monoBuffer.AsSpan(0, BatchThreshold));
            int remain = _batchBytes - BatchThreshold;
            if (remain > 0)
            {
                Buffer.BlockCopy(_monoBuffer, BatchThreshold, _monoBuffer, 0, remain);
            }

            _batchBytes = remain;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
