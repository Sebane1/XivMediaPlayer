using LibVLCSharp.Shared;
using MediaPlayerCore.YtDlp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MediaPlayerCore {
  public class MediaObject : IDisposable {
    private IMediaGameObject _playerObject;
    private IMediaGameObject _camera;
    private SoundType _soundType;
    private bool _spatialAllowed;
    private LibVLC libVLC;
    private MediaPlayer _vlcPlayer;
    private MediaManager _parent;

    private MemoryMappedFile _vlcMappedFile;
    private MemoryMappedViewAccessor _vlcMappedViewAccessor;
    private IntPtr _vlcBuffer = IntPtr.Zero;

    private IWavePlayer _waveOut;
    private WaveFormat _waveFormat;
    private BufferedWaveProvider _bufferedWaveProvider;
    private PanningSampleProvider _panningProvider;
    private VolumeSampleProvider _volumeProvider;
    private bool _isPlayingAudio = false;

    public float Pan {
      get => _panningProvider?.Pan ?? 0;
      set {
        if (_panningProvider != null) {
          _panningProvider.Pan = Math.Clamp(value, -1f, 1f);
        }
      }
    }

    public event EventHandler<MediaError> OnErrorReceived;
    public event EventHandler<string> PlaybackStopped;
    public event EventHandler<string> PlaybackFinished;

    private string _soundPath;
    private string _libVLCPath;

    private uint _width = 1280;
    private uint _height = 720;

    /// <summary>
    /// RGBA is used, so 4 byte per pixel, or 32 bits.
    /// </summary>
    private const uint _bytePerPixel = 4;

    /// <summary>
    /// the number of bytes per "line"
    /// For performance reasons inside the core of VLC, it must be aligned to multiples of 32.
    /// </summary>
    private uint _pitch;

    /// <summary>
    /// The number of lines in the buffer.
    /// For performance reasons inside the core of VLC, it must be aligned to multiples of 32.
    /// </summary>
    private uint _lines;
    private float volumePercentage = 1;
    private float _baseVolume = 1;
    private bool _vlcWasAbleToStart;
    private bool _disposed;
    private bool _trueDimensionsExtracted;
    private bool _isDisposing;
    private readonly object _disposeLock = new object();

    private bool _audioOnly;

    public MediaObject(MediaManager parent, IMediaGameObject playerObject, IMediaGameObject camera,
      SoundType soundType, string soundPath, string libVLCPath, bool spatialAllowed, bool audioOnly = false) {
      _playerObject = playerObject;
      _audioOnly = audioOnly;
      _soundPath = soundPath;
      _camera = camera;
      _libVLCPath = libVLCPath;
      _parent = parent;
      this._soundType = soundType;
      _spatialAllowed = spatialAllowed;
      _pitch = Align(_width * _bytePerPixel);
      _lines = Align(_height);
      _vlcMappedFile = MemoryMappedFile.CreateNew(null, _pitch * _lines);
      _vlcMappedViewAccessor = _vlcMappedFile.CreateViewAccessor();
      _vlcBuffer = _vlcMappedViewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle();
      _parent.OnCleanupTime += _parent_OnCleanupTime;
    }

    private void _parent_OnCleanupTime(object? sender, EventArgs e) {
      Invalidated = true;
    }

    private static uint Align(uint size) {
      if (size % 32 == 0) {
        return size;
      }
      return ((size / 32) + 1) * 32;
    }

    public IMediaGameObject CharacterObject { get => _playerObject; set => _playerObject = value; }
    public float Volume {
      get {
        try {
          if (_vlcPlayer != null) {
            return _vlcPlayer.Volume;
          }
        } catch { }
        return 0;
      }
      set {
        if (_vlcPlayer != null) {
          try {
            float clampedValue = Math.Max(0f, value);
            float scale = clampedValue;
            if (clampedValue <= 1.0f) {
                // Apply a cubic curve below 100% to simulate logarithmic human hearing perception
                scale = (float)Math.Pow(clampedValue, 3);
            } else {
                // Keep linear scaling above 100% (volume boost) to prevent exponential speaker blowouts
                scale = 1.0f + (clampedValue - 1.0f);
            }

            int newValue = (int)(scale * 100f);
            if (newValue != _vlcPlayer.Volume) {
              _baseVolume = newValue;
              _vlcPlayer.Volume = (int)((float)newValue * volumePercentage);
              if (_volumeProvider != null) {
                  _volumeProvider.Volume = ((float)newValue / 100f * volumePercentage) * 2.0f; // Boost spatial audio to match native VLC output
              }
            }
          } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
        }
      }
    }
    public PlaybackState PlaybackState {
      get {
        if (_vlcPlayer != null) {
          try {
            if (_vlcPlayer.State == LibVLCSharp.Shared.VLCState.Playing) return PlaybackState.Playing;
            if (_vlcPlayer.State == LibVLCSharp.Shared.VLCState.Paused) return PlaybackState.Paused;
            return PlaybackState.Stopped;
          } catch {
            return PlaybackState.Stopped;
          }
        } else {
          return PlaybackState.Stopped;
        }
      }
    }
    
    public LibVLCSharp.Shared.VLCState VlcState => _vlcPlayer?.State ?? LibVLCSharp.Shared.VLCState.Stopped;
    
    public long Time {
      get => _vlcPlayer?.Time ?? 0;
      set {
        if (_vlcPlayer != null) {
          try {
            if (!_vlcPlayer.IsSeekable && _vlcPlayer.State == LibVLCSharp.Shared.VLCState.Playing)
            {
              if (YtDlpManager.IsSabrLocalFile(_soundPath))
              {
                ChangeVideoStream(_soundPath, _width, (int)value);
                return;
              }

              return; // Cannot seek, ignore request to prevent stream crash
            }
          } catch {}

          if (_vlcPlayer.State == LibVLCSharp.Shared.VLCState.Ended || _vlcPlayer.State == LibVLCSharp.Shared.VLCState.Stopped) {
            ChangeVideoStream(_soundPath, _width, (int)value);
          } else {
            _vlcPlayer.Time = value;
            _bufferedWaveProvider?.ClearBuffer();
          }
        }
      }
    }

    public long Length => _vlcPlayer?.Length ?? 0;

    public void Pause() {
      _vlcPlayer?.SetPause(true);
    }
    public void Resume() {
      _vlcPlayer?.SetPause(false);
    }
    public SoundType SoundType { get => _soundType; set => _soundType = value; }
    public string SoundPath { get => _soundPath; set => _soundPath = value; }
    public IMediaGameObject Camera { get => _camera; set => _camera = value; }
    public bool Invalidated { get; internal set; }
    public bool SpatialAllowed { get => _spatialAllowed; set => _spatialAllowed = value; }
    public MediaManager Parent { get => _parent; set => _parent = value; }

    public void Stop() {
      PlaybackStopped?.Invoke(this, "OK");
      if (_vlcPlayer != null) {
        try {
          _vlcPlayer?.Stop();
        } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
      }
      Invalidated = true;
    }

    private static bool NeedsVideoCallbacks(string mediaPath, bool audioOnly)
    {
      if (audioOnly) return false;
      return mediaPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        || mediaPath.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase)
        || mediaPath.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)
        || mediaPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        || mediaPath.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)
        || mediaPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNetworkMediaPath(string mediaPath)
      => mediaPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        || mediaPath.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase)
        || mediaPath.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase);

    public void Play(string mediaPath, float volume, int startTimeMs, Dictionary<string, string>? httpHeaders, string? slaveAudioPath = null) {
      Task.Run(async delegate {
        try {
          if (!string.IsNullOrEmpty(mediaPath) && PlaybackState == PlaybackState.Stopped) {
            try {
              _soundPath = mediaPath;
              lock (_parent.FrameLock) {
                _parent.LastFrame = Array.Empty<byte>();
                _parent.LastFrameWidth = 0;
                _parent.LastFrameHeight = 0;
                _parent.LastFrameTrueWidth = 0;
                _parent.LastFrameTrueHeight = 0;
                _parent.LastFrameCount++;
              }
              string location = _libVLCPath + @"\libvlc\win-x64";
              Debug.WriteLine($"[MediaObject] Initializing VLC from: {location}");
              Debug.WriteLine($"[MediaObject] Media path: {mediaPath.Substring(0, Math.Min(100, mediaPath.Length))}...");

              Core.Initialize(location);
              string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

              var vlcArgs = new List<string> {
                "--vout=none", 
                "--http-reconnect"
              };

              libVLC = new LibVLC(vlcArgs.ToArray());

              // Hook VLC's internal log to catch errors
              libVLC.Log += (s, e) => {
                if (e.Level >= LogLevel.Error) {
                  Debug.WriteLine($"[VLC-{e.Level}] {e.Module}: {e.Message}");
                  OnErrorReceived?.Invoke(this, new MediaError() { Exception = new Exception($"VLC [{e.Level}] {e.Module}: {e.Message}") });
                } else if (e.Level >= LogLevel.Warning) {
                  Debug.WriteLine($"[VLC-{e.Level}] {e.Module}: {e.Message}");
                }
              };

              var media = new Media(libVLC, mediaPath, IsNetworkMediaPath(mediaPath)
                ? FromType.FromLocation : FromType.FromPath);
              
              if (_audioOnly) {
                  media.AddOption(":no-video");
              }
              if (!string.IsNullOrEmpty(slaveAudioPath)) {
                  media.AddOption($":input-slave={slaveAudioPath}");
              }

              if (mediaPath.StartsWith("rtsp")) {
                  if (_audioOnly) {
                      media.AddOption(":network-caching=300");
                  } else {
                      media.AddOption(":network-caching=30");
                      media.AddOption(":clock-jitter=0");
                      media.AddOption(":drop-late-frames");
                      media.AddOption(":skip-frames");
                  }
              } else if (YtDlpManager.IsSabrLocalFile(mediaPath)) {
                  media.AddOption(":file-caching=3000");
              } else if (YtDlpManager.IsSabrProxyUrl(mediaPath)) {
                  media.AddOption(":network-caching=5000");
              } else {
                  media.AddOption(":network-caching=2000");
              }
              
              if (startTimeMs > 0) {
                  media.AddOption($":start-time={(startTimeMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
              }

              if (httpHeaders != null && httpHeaders.TryGetValue("User-Agent", out string mediaUserAgent)) {
                  media.AddOption($":http-user-agent={mediaUserAgent}");
              } else {
                  media.AddOption($":http-user-agent={userAgent}");
              }

              if (httpHeaders != null && httpHeaders.TryGetValue("Referer", out string mediaReferer)) {
                  media.AddOption($":http-referrer={mediaReferer}");
              }

              Debug.WriteLine("[MediaObject] Parsing media...");
              await media.Parse(IsNetworkMediaPath(mediaPath)
                ? MediaParseOptions.ParseNetwork : MediaParseOptions.ParseLocal);
              Debug.WriteLine($"[MediaObject] Media parsed. Duration: {media.Duration}ms");
              
              if (media.Tracks != null) {
                  foreach (var track in media.Tracks) {
                      if (track.TrackType == TrackType.Video) {
                          int trueWidth = (int)track.Data.Video.Width;
                          int trueHeight = (int)track.Data.Video.Height;
                          if (trueWidth > 0 && trueHeight > 0 && _parent != null) {
                              _parent.LastFrameTrueWidth = trueWidth;
                              _parent.LastFrameTrueHeight = trueHeight;
                              Debug.WriteLine($"[MediaObject] Extracted true video dimensions from track: {trueWidth}x{trueHeight}");
                          }
                          break;
                      }
                  }
              }

              lock (_disposeLock) {
                if (_disposed) {
                   media.Dispose();
                   return;
                }

                _vlcPlayer = new MediaPlayer(media);
                
                if (_spatialAllowed) {
                    _vlcPlayer.SetAudioFormat("s16l", 48000, 1);
                    _vlcPlayer.SetAudioCallbacks(PlayAudio, PauseAudio, ResumeAudio, FlushAudio, DrainAudio);
                    
                    _waveFormat = new WaveFormat(48000, 16, 1);
                    _bufferedWaveProvider = new BufferedWaveProvider(_waveFormat);
                    _bufferedWaveProvider.BufferDuration = TimeSpan.FromSeconds(10);
                    _bufferedWaveProvider.DiscardOnBufferOverflow = true;
                    
                    _panningProvider = new PanningSampleProvider(_bufferedWaveProvider.ToSampleProvider());
                    _panningProvider.Pan = 0;
                    
                    _volumeProvider = new VolumeSampleProvider(_panningProvider);
                    _volumeProvider.Volume = (_baseVolume / 100f) * 2.0f; // Scale 0-100 to 0-1, boosted for spatial compensation
                    
                    _waveOut = new WasapiOut(AudioClientShareMode.Shared, 150);
                    _waveOut.Init(_volumeProvider);
                }

                _vlcPlayer.Stopped += delegate {
                  lock (_parent.FrameLock) {
                    _parent.LastFrame = Array.Empty<byte>();
                    _parent.LastFrameWidth = 0;
                _parent.LastFrameHeight = 0;
                _parent.LastFrameTrueWidth = 0;
                _parent.LastFrameTrueHeight = 0;
                    _parent.LastFrameCount++;
                  }
                };
                _vlcPlayer.EndReached += delegate {
                  PlaybackFinished?.Invoke(this, "OK");
                };
                _vlcPlayer.EncounteredError += (s, e) => {
                  Debug.WriteLine("[MediaObject] VLC EncounteredError event fired!");
                  OnErrorReceived?.Invoke(this, new MediaError() { Exception = new Exception("VLC player encountered an error during playback.") });
                };
                if (NeedsVideoCallbacks(mediaPath, _audioOnly)) {
                    _vlcPlayer.SetVideoFormatCallbacks(VideoFormatSetup, null);
                    _vlcPlayer.SetVideoCallbacks(Lock, null, Display);
                }
              }

              _baseVolume = volume;
              Volume = volume;
              
              long exactSeekMs = startTimeMs;
              _vlcPlayer.Playing += (s, e) => {
                  if (exactSeekMs > 0) {
                      // Fire exact seek to correct keyframe snapping margin of error
                      Task.Run(async () => {
                        await Task.Delay(2000); // Bypass plugin load lag spike
                            if (_vlcPlayer != null) {
                                _vlcPlayer.Time = exactSeekMs;
                                _bufferedWaveProvider?.ClearBuffer();
                            }
                        exactSeekMs = 0;
                    });
                  }
              };

              bool playResult = _vlcPlayer.Play();
              Debug.WriteLine($"[MediaObject] VLC Play() returned: {playResult}");
              _vlcWasAbleToStart = playResult;

              if (!playResult) {
                OnErrorReceived?.Invoke(this, new MediaError() { Exception = new Exception("VLC Play() returned false — playback failed to start.") });
              }
            } catch (Exception e) {
              Debug.WriteLine($"[MediaObject] Play exception: {e}");
              OnErrorReceived?.Invoke(this, new MediaError() { Exception = e });
              PlaybackStopped?.Invoke(this, "OK");
            }
          } else {
            Debug.WriteLine($"[MediaObject] Play skipped. mediaPath empty={string.IsNullOrEmpty(mediaPath)}, state={PlaybackState}");
          }
        } catch (Exception e) {
          Debug.WriteLine($"[MediaObject] Outer play exception: {e}");
          OnErrorReceived?.Invoke(this, new MediaError() { Exception = e });
          PlaybackStopped?.Invoke(this, "ERR");
        }
      });
    }

    public void ChangeVideoStream(string soundPath, float width, int startTimeMs = 0, Dictionary<string, string>? httpHeaders = null, string? slaveAudioPath = null) {
      Task.Run(async delegate {
        try {
          if (_vlcPlayer != null) {
            _soundPath = soundPath;
            var media = new Media(libVLC, soundPath, IsNetworkMediaPath(soundPath)
                     ? FromType.FromLocation : FromType.FromPath);
            
            if (_audioOnly) {
                  media.AddOption(":no-video");
              }
              if (!string.IsNullOrEmpty(slaveAudioPath)) {
                  media.AddOption($":input-slave={slaveAudioPath}");
              }

            if (soundPath.StartsWith("rtsp")) {
                media.AddOption(":network-caching=30");
                media.AddOption(":clock-jitter=0");
                media.AddOption(":drop-late-frames");
                media.AddOption(":skip-frames");
            } else if (YtDlpManager.IsSabrLocalFile(soundPath)) {
                media.AddOption(":file-caching=3000");
            } else if (YtDlpManager.IsSabrProxyUrl(soundPath)) {
                media.AddOption(":network-caching=5000");
            } else {
                media.AddOption(":network-caching=2000");
            }
            
            if (startTimeMs > 0) {
                media.AddOption($":start-time={(startTimeMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            if (httpHeaders != null && httpHeaders.TryGetValue("User-Agent", out string mediaUserAgent)) {
                media.AddOption($":http-user-agent={mediaUserAgent}");
            } else if (IsNetworkMediaPath(soundPath)) {
                media.AddOption($":http-user-agent={userAgent}");
            }

            if (httpHeaders != null && httpHeaders.TryGetValue("Referer", out string mediaReferer)) {
                media.AddOption($":http-referrer={mediaReferer}");
            }

            await media.Parse(IsNetworkMediaPath(soundPath)
              ? MediaParseOptions.ParseNetwork : MediaParseOptions.ParseLocal);
            
            MediaPlayer playerToStop = null;
            lock (_disposeLock) {
                if (_disposed) {
                    media.Dispose();
                    return;
                }
                playerToStop = _vlcPlayer;
            }

            // Explicitly stop the previous media before assigning the new one to prevent VLC from deadlocking
            // Do NOT hold _disposeLock while stopping!
            if (playerToStop != null) {
                try { playerToStop.Stop(); } catch { }
                _bufferedWaveProvider?.ClearBuffer();
                await Task.Delay(250); // Allow LibVLC background thread to complete teardown
            }

            lock (_disposeLock) {
                if (_disposed) {
                    media.Dispose();
                    return;
                }
                if (_vlcPlayer != null) {
                    _trueDimensionsExtracted = false;
                    _vlcPlayer.Media = media;
                    if (NeedsVideoCallbacks(soundPath, _audioOnly)) {
                        _vlcPlayer.SetVideoFormatCallbacks(VideoFormatSetup, null);
                        _vlcPlayer.SetVideoCallbacks(Lock, null, Display);
                    }
                }
            }

            long exactSeekMs = startTimeMs;
            EventHandler<EventArgs> playingHandler = null;
            playingHandler = (s, e) => {
                if (exactSeekMs > 0) {
                    Task.Run(async () => {
                        await Task.Delay(2000); // Bypass plugin load lag spike
                          if (_vlcPlayer != null && !_disposed) {
                              _vlcPlayer.Time = exactSeekMs;
                              _bufferedWaveProvider?.ClearBuffer();
                          }
                        exactSeekMs = 0;
                    });
                }
                if (_vlcPlayer != null) {
                    _vlcPlayer.Playing -= playingHandler;
                }
            };
            _vlcPlayer.Playing += playingHandler;

            _vlcPlayer.Play();
          }
        } catch (Exception e) { OnErrorReceived?.Invoke(this, new MediaError() { Exception = e }); }
      });
    }

    public static float AngleDir(Vector3 fwd, Vector3 targetDir, Vector3 up) {
      Vector3 perp = Vector3.Cross(fwd, targetDir);
      float dir = Vector3.Dot(perp, up);
      return dir;
    }

    private IntPtr Lock(IntPtr opaque, IntPtr planes) {
      try {
        if (_vlcBuffer != IntPtr.Zero) {
            Marshal.WriteIntPtr(planes, _vlcBuffer);
        }
        return IntPtr.Zero;
      } catch {
        return IntPtr.Zero;
      }
    }

    public void ResetVolume() {
      // No-op for VLC-only path; volume is managed through the VLC player directly.
    }

      private void DrainAudio(IntPtr data) {
      }

      private void FlushAudio(IntPtr data, long pts) {
          // Explicitly clear the buffer on flush so that stale audio from before a seek isn't played.
          _bufferedWaveProvider?.ClearBuffer();
      }

      private void ResumeAudio(IntPtr data, long pts) {
          _waveOut?.Play();
      }

      private void PauseAudio(IntPtr data, long pts) {
          _waveOut?.Pause();
      }

        private void PlayAudio(IntPtr data, IntPtr samples, uint count, long pts) {
            if (_bufferedWaveProvider != null && _waveFormat != null) {
                int bytes = (int)count * _waveFormat.BlockAlign;
                byte[] buffer = new byte[bytes];
                Marshal.Copy(samples, buffer, 0, bytes);
                _bufferedWaveProvider.AddSamples(buffer, 0, bytes);

                if (_waveOut != null) {
                    if (_waveOut.PlaybackState != PlaybackState.Playing) {
                        // Wait until we have a healthy 300ms cushion before starting audio playback
                        if (_bufferedWaveProvider.BufferedDuration.TotalMilliseconds > 300) {
                            _waveOut.Play();
                        }
                    }
                }
            }
        }

      private void Display(IntPtr opaque, IntPtr picture) {
        lock (_disposeLock) {
            if (_disposed) return;
            try {
              lock (_parent.FrameLock) {
                int totalBytes = (int)(_pitch * _lines);
                if (_parent.LastFrame.Length != totalBytes) {
                    _parent.LastFrame = new byte[totalBytes];
                }
                Marshal.Copy(_vlcBuffer, _parent.LastFrame, 0, totalBytes);
                
                unsafe {
                    fixed (byte* ptr = _parent.LastFrame) {
                        for (int i = 3; i < totalBytes; i += 4) {
                            ptr[i] = 255;
                        }
                    }
                }
                
                _parent.LastFrameWidth = (int)(_pitch / _bytePerPixel);
                _parent.LastFrameHeight = (int)_lines;
                if (_parent.LastFrameTrueWidth == 0 || _parent.LastFrameTrueHeight == 0) {
              _parent.LastFrameTrueWidth = (int)_width;
              _parent.LastFrameTrueHeight = (int)_height;
          }
                _parent.LastFrameCount++;
              }
            } catch (Exception ex) {
                Debug.WriteLine($"[MediaObject] Display error: {ex}");
            }
        }
      }

      private uint VideoFormatSetup(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines) {
        byte[] rv32 = System.Text.Encoding.ASCII.GetBytes("RV32");
        Marshal.Copy(rv32, 0, chroma, 4);
        
        _width = width;
        _height = height;
        _pitch = Align(_width * _bytePerPixel);
        _lines = Align(_height);
        
        pitches = _pitch;
        lines = _lines;
        
        lock (_disposeLock) {
          if (!_disposed) {
            if (_vlcMappedViewAccessor != null) {
              _vlcMappedViewAccessor.Dispose();
            }
            if (_vlcMappedFile != null) {
              _vlcMappedFile.Dispose();
            }
            _vlcMappedFile = MemoryMappedFile.CreateNew(null, _pitch * _lines);
            _vlcMappedViewAccessor = _vlcMappedFile.CreateViewAccessor();
            _vlcBuffer = _vlcMappedViewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle();
          }
        }
        
        if (_parent != null && !_trueDimensionsExtracted) {
            Task.Run(async () => {
                while (!_trueDimensionsExtracted && _vlcPlayer != null && !_disposed) {
                    uint px = 0, py = 0;
                    if (_vlcPlayer.Size(0, ref px, ref py)) {
                        if (px > 0 && py > 0 && Math.Abs((long)px - _width) <= 64 && Math.Abs((long)py - _height) <= 64) {
                            _parent.LastFrameTrueWidth = (int)px;
                            _parent.LastFrameTrueHeight = (int)py;
                            _trueDimensionsExtracted = true;
                            System.Diagnostics.Debug.WriteLine($"[MediaObject] MediaPlayer.Size polled true dimensions: {px}x{py}");
                        }
                    }
                    if (!_trueDimensionsExtracted && _vlcPlayer.Media != null) {
                        var tracks = _vlcPlayer.Media.Tracks;
                        if (tracks != null) {
                            foreach (var track in tracks) {
                                if (track.TrackType == LibVLCSharp.Shared.TrackType.Video) {
                                    int trueWidth = (int)track.Data.Video.Width;
                                    int trueHeight = (int)track.Data.Video.Height;
                                    if (trueWidth > 0 && trueHeight > 0 && Math.Abs((long)trueWidth - _width) <= 64 && Math.Abs((long)trueHeight - _height) <= 64) {
                                        _parent.LastFrameTrueWidth = trueWidth;
                                        _parent.LastFrameTrueHeight = trueHeight;
                                        _trueDimensionsExtracted = true;
                                        System.Diagnostics.Debug.WriteLine($"[MediaObject] Task extracted true dimensions: {trueWidth}x{trueHeight}");
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    if (_trueDimensionsExtracted) break;
                    await Task.Delay(500);
                }
            });
        }
        
        return 1;
      }

    public void Dispose() {
      MediaPlayer playerToStop = null;
      LibVLC libVlcToDispose = null;

      lock (_disposeLock) {
          if (_disposed || _isDisposing) {
            return;
          }
          _isDisposing = true;
          
          playerToStop = _vlcPlayer;
          libVlcToDispose = libVLC;
      }

      if (playerToStop != null) {
          PlaybackStopped?.Invoke(this, "OK");
          try { playerToStop.Stop(); } catch { }
          try { playerToStop.Dispose(); } catch { }
      }
      if (libVlcToDispose != null) {
          try { libVlcToDispose.Dispose(); } catch { }
      }

      lock (_disposeLock) {
          if (_disposed) return;
          _disposed = true;
          _parent.OnCleanupTime -= _parent_OnCleanupTime;
          
          _vlcPlayer = null;
          libVLC = null;

          if (_vlcMappedViewAccessor != null) {
              _vlcMappedViewAccessor.Dispose();
              _vlcMappedViewAccessor = null;
          }
          if (_vlcMappedFile != null) {
              _vlcMappedFile.Dispose();
              _vlcMappedFile = null;
          }
          _vlcBuffer = IntPtr.Zero;

          if (_waveOut != null) {
              _waveOut.Stop();
              _waveOut.Dispose();
              _waveOut = null;
          }
      }
    }
  }
}





