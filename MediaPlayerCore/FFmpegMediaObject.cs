using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;

namespace MediaPlayerCore
{
    public class FFmpegMediaObject : IDisposable
    {
        private MediaManager _parent;
        private Process _ffmpegProcess;
        private Process _ffmpegAudioProcess;
        private string _ffmpegPath;
        private bool _disposed;
        private readonly object _disposeLock = new object();

        public IMediaGameObject CharacterObject { get; set; }
        public bool SpatialAllowed { get; set; }

        private IWavePlayer _waveOut;
        private WaveFormat _waveFormat;
        private BufferedWaveProvider _bufferedWaveProvider;
        private PanningSampleProvider _panningProvider;
        private VolumeSampleProvider _volumeProvider;

        public float Pan {
            get => _panningProvider?.Pan ?? 0;
            set {
                if (_panningProvider != null) {
                    _panningProvider.Pan = Math.Clamp(value, -1f, 1f);
                }
            }
        }

        public float Volume {
            set {
                if (_volumeProvider != null) {
                    float clampedValue = Math.Max(0f, value);
                    float scale = clampedValue;
                    if (clampedValue <= 1.0f) {
                        scale = (float)Math.Pow(clampedValue, 3);
                    } else {
                        scale = 1.0f + (clampedValue - 1.0f);
                    }
                    _volumeProvider.Volume = scale * 2.0f;
                }
            }
        }

        private int _width = 1920;
        private int _height = 1080;
        private int _bytesPerPixel = 4;
        private int _frameSize;
        private Thread _readerThread;
        private System.Net.Sockets.TcpListener _audioTcpListener;
        private System.Net.Sockets.TcpListener _videoTcpListener;

        public event EventHandler<string> PlaybackStopped;
        public event EventHandler<MediaError> OnErrorReceived;
        public bool IsPlaying {
            get {
                try {
                    return _ffmpegProcess != null && !_ffmpegProcess.HasExited;
                } catch {
                    return false;
                }
            }
        }

        public FFmpegMediaObject(MediaManager parent, string ffmpegPath)
        {
            _parent = parent;
            _ffmpegPath = ffmpegPath;
            _frameSize = _width * _height * _bytesPerPixel;
            _parent.OnCleanupTime += _parent_OnCleanupTime;
        }

        private void _parent_OnCleanupTime(object? sender, EventArgs e)
        {
            Stop();
        }

        public void Play(string url, IMediaGameObject characterObject = null, bool spatialAllowed = false)
        {
            lock (_disposeLock)
            {
                if (_disposed) return;

                CharacterObject = characterObject;
                SpatialAllowed = spatialAllowed;

                try
                {
                    Stop(true);
                    _disposed = false;

                    lock (_parent.FrameLock)
                    {
                        _parent.LastFrame = new byte[_frameSize];
                        _parent.LastFrameWidth = _width;
                          _parent.LastFrameHeight = _height;
                          _parent.LastFrameTrueWidth = _width;
                          _parent.LastFrameTrueHeight = _height;
                        _parent.LastFrameCount++;
                    }

                    Task.Run(() =>
                    {
                        // Generate local TCP ports for video and audio
                        _audioTcpListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                        _audioTcpListener.Start();
                        int audioPort = ((System.Net.IPEndPoint)_audioTcpListener.LocalEndpoint).Port;

                        _videoTcpListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                        _videoTcpListener.Start();
                        int videoPort = ((System.Net.IPEndPoint)_videoTcpListener.LocalEndpoint).Port;

                        _ffmpegProcess = new Process();
                        _ffmpegProcess.StartInfo.FileName = _ffmpegPath;
                        // Single process decoding both video and audio over TCP to enforce synchronous backpressure
                        _ffmpegProcess.StartInfo.Arguments = $"-hwaccel auto -rtsp_transport tcp -fflags nobuffer -flags low_delay -i \"{url}\" -map 0:v -r 60 -s {_width}x{_height} -f rawvideo -pix_fmt bgra tcp://127.0.0.1:{videoPort} -map 0:a? -f s16le -ac 1 -ar 48000 tcp://127.0.0.1:{audioPort}";
                        _ffmpegProcess.StartInfo.UseShellExecute = false;
                        _ffmpegProcess.StartInfo.RedirectStandardOutput = false;
                        _ffmpegProcess.StartInfo.RedirectStandardError = true;
                        _ffmpegProcess.StartInfo.CreateNoWindow = true;

                        _ffmpegProcess.ErrorDataReceived += (s, e) =>
                        {
                            if (e.Data != null)
                                Debug.WriteLine($"[FFmpegMediaObject] {e.Data}");
                        };

                        _waveFormat = new WaveFormat(48000, 16, 1);
                        _bufferedWaveProvider = new BufferedWaveProvider(_waveFormat);
                        _bufferedWaveProvider.BufferDuration = TimeSpan.FromSeconds(10);
                        _bufferedWaveProvider.DiscardOnBufferOverflow = true;

                        _panningProvider = new PanningSampleProvider(_bufferedWaveProvider.ToSampleProvider());
                        _panningProvider.Pan = 0;

                        _volumeProvider = new VolumeSampleProvider(_panningProvider);
                        _volumeProvider.Volume = 1.0f;

                        TryInitSpatialAudioOutput(_volumeProvider, latencyMs: 50);

                        _ffmpegProcess.Start();
                        try { _ffmpegProcess.PriorityClass = ProcessPriorityClass.High; } catch { }
                        _ffmpegProcess.BeginErrorReadLine();

                        Task.Run(() => {
                            Thread.CurrentThread.Priority = ThreadPriority.Highest;
                            try {
                                while (!_videoTcpListener.Pending()) {
                                    if (_disposed || (_ffmpegProcess != null && _ffmpegProcess.HasExited)) return;
                                    Thread.Sleep(10);
                                }
                                using var videoClient = _videoTcpListener.AcceptTcpClient();
                                _videoTcpListener.Stop();
                                videoClient.ReceiveBufferSize = 8388608; // 8 MB buffer to prevent blocking
                                var stream = videoClient.GetStream();
                                byte[] buffer = new byte[_frameSize];

                                while (!_disposed && !_ffmpegProcess.HasExited)
                                {
                                    int bytesRead = 0;
                                    while (bytesRead < _frameSize && !_disposed)
                                    {
                                        int read = stream.Read(buffer, bytesRead, _frameSize - bytesRead);
                                        if (read == 0) break;
                                        bytesRead += read;
                                    }

                                    if (bytesRead == _frameSize && !_disposed)
                                    {
                                          lock (_parent.FrameLock)
                                          {
                                              // Zero-copy pointer swap!
                                              var temp = _parent.LastFrame;
                                              _parent.LastFrame = buffer;
                                              buffer = temp;
                                              if (buffer.Length != _frameSize) buffer = new byte[_frameSize];

                                              _parent.LastFrameWidth = _width;
                          _parent.LastFrameHeight = _height;
                          _parent.LastFrameTrueWidth = _width;
                          _parent.LastFrameTrueHeight = _height;
                                            _parent.LastFrameCount++;
                                        }
                                    }
                                    else break;
                                }
                            } catch { }
                            finally {
                                Stop();
                            }
                        });

                        Task.Run(() => {
                            Thread.CurrentThread.Priority = ThreadPriority.Highest;
                            try {
                                while (!_audioTcpListener.Pending()) {
                                    if (_disposed || (_ffmpegProcess != null && _ffmpegProcess.HasExited)) return;
                                    Thread.Sleep(10);
                                }
                                using var audioClient = _audioTcpListener.AcceptTcpClient();
                                _audioTcpListener.Stop();
                                var stream = audioClient.GetStream();
                                byte[] buffer = new byte[8192];

                                bool startedPlaying = false;
                                while (!_disposed && !_ffmpegProcess.HasExited) {
                                    int read = stream.Read(buffer, 0, buffer.Length);
                                    if (read > 0 && !_disposed) {
                                        // If the audio buffer has grown too large (desync), clear it to snap back to live
                                        if (_bufferedWaveProvider.BufferedDuration.TotalMilliseconds > 150) {
                                            _bufferedWaveProvider.ClearBuffer();
                                        }

                                        _bufferedWaveProvider.AddSamples(buffer, 0, read);
                                        if (!startedPlaying && _bufferedWaveProvider.BufferedDuration.TotalMilliseconds > 50) {
                                            _waveOut.Play();
                                            startedPlaying = true;
                                        }
                                    } else if (read == 0) {
                                        break;
                                    }
                                }
                            } catch { }
                        });
                    });
                }
                catch (Exception ex)
                {
                    OnErrorReceived?.Invoke(this, new MediaError() { Exception = ex });
                }
            }
        }


        public void Stop(bool silent = false)
        {
            lock (_disposeLock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            if (!silent)
            {
                PlaybackStopped?.Invoke(this, "OK");
            }

            try {
                if (_audioTcpListener != null) {
                    _audioTcpListener.Stop();
                    _audioTcpListener = null;
                }
                if (_videoTcpListener != null) {
                    _videoTcpListener.Stop();
                    _videoTcpListener = null;
                }
            } catch { }

            try
            {
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill();
                    _ffmpegProcess.Dispose();
                }
            }
            catch { }

            try {
                if (_waveOut != null) {
                    _waveOut.Stop();
                    _waveOut.Dispose();
                    _waveOut = null;
                }
            } catch { }

            lock (_parent.FrameLock)
            {
                _parent.LastFrame = Array.Empty<byte>();
                _parent.LastFrameWidth = 0;
                  _parent.LastFrameHeight = 0;
                  _parent.LastFrameTrueWidth = 0;
                  _parent.LastFrameTrueHeight = 0;
                _parent.LastFrameCount++;
            }
        }

        public void Dispose()
        {
            _parent.OnCleanupTime -= _parent_OnCleanupTime;
            Stop();
            _ffmpegProcess?.Dispose();
        }

        private void TryInitSpatialAudioOutput(ISampleProvider sampleProvider, int latencyMs)
        {
            try
            {
                var wasapi = new WasapiOut(AudioClientShareMode.Shared, latencyMs);
                wasapi.Init(sampleProvider);
                _waveOut = wasapi;
            }
            catch (Exception ex) when (ex is InvalidCastException or System.Runtime.InteropServices.COMException)
            {
                Debug.WriteLine($"[FFmpegMediaObject] WasapiOut unavailable ({ex.GetType().Name}), using WaveOutEvent.");
                var waveOut = new WaveOutEvent();
                waveOut.Init(sampleProvider);
                _waveOut = waveOut;
            }
        }
    }
}

