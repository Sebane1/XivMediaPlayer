using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;

using MediaPlayerCore;
using MediaPlayerCore.Twitch;
using System;
using System.Diagnostics;
using Vector2 = System.Numerics.Vector2;

namespace XivMediaPlayer.Windows {
  internal class VideoWindow : Window {
    private System.Numerics.Vector2? windowSize;
    private Vector2? initialSize;
    IDalamudTextureWrap textureWrap;
    MediaManager _mediaManager;
    private Plugin _plugin;
    private IDalamudPluginInterface _pluginInterface;
    private ITextureProvider _textureProvider;
    private IPluginLog _pluginLog;
    Stopwatch deadStreamTimer = new Stopwatch();
    private string fpsCount = "";
    int countedFrames = 0;
    private bool wasStreaming;
    private Vector2? _lastWindowSize;
    public event EventHandler WindowResized;
    public TwitchFeedType FeedType = TwitchFeedType._360p;
    private bool _wasNotOpen;
    Stopwatch eventTriggerCooldown = new Stopwatch();
    private Direct3D11VideoTexture _videoTexture;
    private IDalamudTextureWrap _blackFrame;
    private ulong _lastLoadedFrameCount = 0;
    private byte[] _lastLoadedFrame;
    private bool _disposed;
    private readonly object _textureLock = new object();
    
    private bool _isDraggingSeek;
    private float _seekDragProgress;

    public VideoWindow(Plugin plugin, IDalamudPluginInterface pluginInterface, ITextureProvider textureProvider, IPluginLog pluginLog) :
      base("Media Player", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoFocusOnAppearing, false) {
      windowSize = Size = new Vector2(640, 360 + 60);
      this.SizeCondition = ImGuiCond.FirstUseEver;
      this.SizeConstraints = new WindowSizeConstraints() { MinimumSize = new Vector2(360, 260), MaximumSize = new Vector2(3840, 2160) };
      initialSize = Size;
      _plugin = plugin;
      _pluginInterface = pluginInterface;
      _textureProvider = textureProvider;
      _pluginLog = pluginLog;
      Position = new Vector2(0, 0);
      PositionCondition = ImGuiCond.Once;
      eventTriggerCooldown.Start();
      CreateBlackFrame();
    }

    public MediaManager MediaManager { get => _mediaManager; set => _mediaManager = value; }

    /// <summary>
    /// Decodes the latest VLC frame into a texture.
    /// Called every frame from Plugin.OnDraw(), regardless of window visibility,
    /// so the world renderer can get fresh frames even when the ImGui window is closed.
    /// </summary>
        private byte[] _localFrameBuffer = Array.Empty<byte>();

        public void UpdateFrame() {
      if (_disposed || _mediaManager == null) return;

      try {
        bool needsUpdate = false;
        ulong frameCount = 0;
        int frameWidth = 0;
          int frameHeight = 0;
          int trueWidth = 0;
          int trueHeight = 0;

        lock (_mediaManager.FrameLock) {
          frameCount = _mediaManager.LastFrameCount;
          frameWidth = _mediaManager.LastFrameWidth;
            frameHeight = _mediaManager.LastFrameHeight;
            trueWidth = _mediaManager.LastFrameTrueWidth;
            trueHeight = _mediaManager.LastFrameTrueHeight;

          if (frameWidth == 0 || frameHeight == 0 || _mediaManager.LastFrame == null || _mediaManager.LastFrame.Length == 0) {
            if (wasStreaming) {
              if (!deadStreamTimer.IsRunning) {
                deadStreamTimer.Start();
              }
              if (deadStreamTimer.ElapsedMilliseconds > 10000) {
                deadStreamTimer.Stop();
                deadStreamTimer.Reset();
                IsOpen = false;
                wasStreaming = false;
              }
            }
            return;
          }

          wasStreaming = true;
          if (deadStreamTimer.IsRunning) {
            deadStreamTimer.Stop();
            deadStreamTimer.Reset();
          }

          if (_lastLoadedFrameCount != frameCount) {
             if (_localFrameBuffer.Length != _mediaManager.LastFrame.Length) {
                 _localFrameBuffer = new byte[_mediaManager.LastFrame.Length];
             }
             // Zero-copy pointer swap!
             var temp = _localFrameBuffer;
             _localFrameBuffer = _mediaManager.LastFrame;
             _mediaManager.LastFrame = temp;
             needsUpdate = true;
          }
        }

        if (needsUpdate) {
            lock (_textureLock) {
              if (_disposed) return;
              if (_videoTexture == null || _videoTexture.Width != frameWidth || _videoTexture.Height != frameHeight) {
                _videoTexture?.Dispose();
                _videoTexture = new Direct3D11VideoTexture(frameWidth, frameHeight);
              }
              _videoTexture.Update(_localFrameBuffer, frameWidth, frameHeight);
              _lastLoadedFrameCount = frameCount;
            }
        }

        // Keep true dimensions in sync even between GPU uploads (needed for aspect ratio).
        if (_videoTexture != null && (trueWidth > 0 || trueHeight > 0)) {
            lock (_textureLock) {
              if (_disposed || _videoTexture == null) return;
              if (trueWidth > 0) _videoTexture.TrueWidth = trueWidth;
              if (trueHeight > 0) _videoTexture.TrueHeight = trueHeight;
            }
        }
      } catch (Exception e) {
        _pluginLog.Warning(e, e.Message);
      }
    }

    public override void Draw() {
      bool betweenAreas = false;
      unsafe {
        betweenAreas = !Conditions.Instance()->BetweenAreas;
      }
      if (IsOpen && betweenAreas && !_disposed) {
        float uiHeight = ImGui.GetTextLineHeightWithSpacing() * 3 + 24; // Allocate space for controls
        
        Vector2 avail = ImGui.GetContentRegionAvail();
        IntPtr currentSrv = IntPtr.Zero;
        Dalamud.Bindings.ImGui.ImTextureID currentId = default;
          int texW = 1920;
          int texH = 1080;
          int trueTexW = 0;
          int trueTexH = 0;
          lock (_textureLock) {
            if (_videoTexture != null && _videoTexture.ImGuiHandle != IntPtr.Zero) {
                currentSrv = _videoTexture.ImGuiHandle;
                currentId = System.Runtime.CompilerServices.Unsafe.As<IntPtr, Dalamud.Bindings.ImGui.ImTextureID>(ref currentSrv);
                  texW = _videoTexture.Width;
                  texH = _videoTexture.Height;
                  trueTexW = _videoTexture.TrueWidth;
                  trueTexH = _videoTexture.TrueHeight;
            } else if (_blackFrame != null) {
                var handle = _blackFrame.Handle;
                currentId = handle;
                currentSrv = System.Runtime.CompilerServices.Unsafe.As<Dalamud.Bindings.ImGui.ImTextureID, IntPtr>(ref handle);
            }
        }
        
        if (currentSrv != IntPtr.Zero) {
          float maxVidWidth = avail.X;
          float maxVidHeight = Math.Max(10f, avail.Y - uiHeight);

          float trueVidHeight = trueTexH > 0 ? trueTexH : 0;
            float trueVidWidth = trueTexW > 0 ? trueTexW : texW;
            float uvBottom = (trueTexH > 0 && texH > 0) ? (float)trueTexH / texH : 1.0f;
            float uvRight = (trueTexW > 0 && texW > 0) ? (float)trueTexW / texW : 1.0f;
            float aspect = (trueVidHeight > 0 && trueVidWidth > 0) ? trueVidWidth / trueVidHeight : 1.7777f;
            float inverseAspect = 1.0f / aspect;
            
            float targetWidth = maxVidWidth;
            float targetHeight = targetWidth * inverseAspect;
  
            if (targetHeight > maxVidHeight) {
                targetHeight = maxVidHeight;
                targetWidth = targetHeight * aspect;
            }

          float offsetX = (maxVidWidth - targetWidth) / 2.0f;
          if (offsetX > 0) {
              ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);
          }

          Vector2 p0 = ImGui.GetCursorScreenPos();
          Vector2 imageSize = new Vector2(targetWidth, targetHeight);
            ImGui.Image(currentId, imageSize, new Vector2(0, 0), new Vector2(uvRight, uvBottom));

          if (_plugin.IsMediaLoading) {
            DrawLoadingOverlay(p0, imageSize);
          }

          if (ImGui.IsItemHovered()) {
              Vector2 mouse = ImGui.GetMousePos();
              float normX = Math.Clamp((mouse.X - p0.X) / imageSize.X, 0f, 1f);
              float normY = Math.Clamp((mouse.Y - p0.Y) / imageSize.Y, 0f, 1f);
              
              bool lmb = ImGui.IsMouseDown(Dalamud.Bindings.ImGui.ImGuiMouseButton.Left);
              bool rmb = ImGui.IsMouseDown(Dalamud.Bindings.ImGui.ImGuiMouseButton.Right);
              float scroll = ImGui.GetIO().MouseWheel;
              
              _plugin.SendEmulationMouseState(normX, normY, scroll, lmb, rmb);
          }
        } else if (_plugin?.IsMediaLoading == true) {
          float maxVidWidth = avail.X;
          float maxVidHeight = Math.Max(10f, avail.Y - uiHeight);
          Vector2 p0 = ImGui.GetCursorScreenPos();
          Vector2 imageSize = new Vector2(maxVidWidth, maxVidHeight);
          DrawLoadingOverlay(p0, imageSize);
          ImGui.Dummy(imageSize);
        }

        //  Seek Slider (VODs only) 
        if (_mediaManager != null && _plugin != null) {
          var activeStream = _mediaManager.ActiveStream;
          long durationMs = _plugin.GetPlaybackDurationMs();
          if (activeStream != null && durationMs > 0) {
            float progress;
            if (!_isDraggingSeek) {
                _seekDragProgress = (float)activeStream.Time / durationMs;
            }
            progress = _seekDragProgress;

            ImGui.SetNextItemWidth(-1);
            
            // Format timecode based on drag progress if dragging, else use actual time
            long displayTime = _isDraggingSeek ? (long)(progress * durationMs) : activeStream.Time;
            
            if (ImGui.SliderFloat("##seek", ref progress, 0f, 1f, 
                FormatTimeCode(displayTime) + " / " + FormatTimeCode(durationMs))) {
              _seekDragProgress = progress;
            }

            if (ImGui.IsItemActivated()) {
                _isDraggingSeek = true;
            }
            
            if (ImGui.IsItemDeactivatedAfterEdit() || (ImGui.IsItemDeactivated() && _isDraggingSeek)) {
                _isDraggingSeek = false;
                activeStream.Time = (long)(_seekDragProgress * durationMs);
            }
          }
        }

        //  Transport Controls 
        if (_plugin != null) {
          float btnW = 36;
          float btnH = 24;
          var btnSize = new Vector2(btnW, btnH);
          var wideBtnSize = new Vector2(48, btnH);

          // Rewind
          if (ImGui.Button("<<", btnSize)) {
            _plugin.SeekRelative(-_plugin.Config.SeekIncrementSeconds);
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Rewind {_plugin.Config.SeekIncrementSeconds}s");

          ImGui.SameLine();

          // Play/Pause
          bool isPaused = _plugin.IsIntentionallyPaused;
          string playPauseLabel = isPaused ? " > ##pp" : " || ##pp";
          if (ImGui.Button(playPauseLabel, btnSize)) {
            _plugin.TogglePlayPause();
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip(isPaused ? "Resume" : "Pause");

          ImGui.SameLine();

          // Fast Forward
          if (ImGui.Button(">>", btnSize)) {
            _plugin.SeekRelative(_plugin.Config.SeekIncrementSeconds);
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Fast Forward {_plugin.Config.SeekIncrementSeconds}s");

          ImGui.SameLine();
          ImGui.Dummy(new Vector2(8, 0));
          ImGui.SameLine();

          // Previous
          if (ImGui.Button("|<", btnSize)) {
            _plugin.PlayPrevious();
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("Previous Track");

          ImGui.SameLine();

          // Next
          if (ImGui.Button(">|", btnSize)) {
            _plugin.PlayNext();
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("Next Track");

          ImGui.SameLine();
          ImGui.Dummy(new Vector2(8, 0));
          ImGui.SameLine();

          // Mute
          string muteLabel = _plugin.IsMuted ? "Unmute" : "Mute";
          if (ImGui.Button(muteLabel, wideBtnSize)) {
            _plugin.ToggleMute();
          }

          ImGui.SameLine();

          // Loop
          bool loopOn = _plugin.Config.LoopEnabled;
          if (loopOn) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.2f, 0.6f, 0.9f, 1f));
          if (ImGui.Button("Loop", wideBtnSize)) {
            _plugin.Config.LoopEnabled = !_plugin.Config.LoopEnabled;
            _plugin.Config.Save();
          }
          if (loopOn) ImGui.PopStyleColor();
          if (ImGui.IsItemHovered()) ImGui.SetTooltip(loopOn ? "Loop: ON" : "Loop: OFF");

          ImGui.SameLine();

          // Shuffle
          bool shuffleOn = _plugin.Config.ShuffleEnabled;
          if (shuffleOn) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.2f, 0.6f, 0.9f, 1f));
          if (ImGui.Button("Shuf", wideBtnSize)) {
            _plugin.Config.ShuffleEnabled = !_plugin.Config.ShuffleEnabled;
            _plugin.Config.Save();
          }
          if (shuffleOn) ImGui.PopStyleColor();
          if (ImGui.IsItemHovered()) ImGui.SetTooltip(shuffleOn ? "Shuffle: ON" : "Shuffle: OFF");

          ImGui.SameLine();

          // Refresh
          if (ImGui.Button("Refresh", new Vector2(56, btnH))) {
            _plugin.RefreshCurrentMedia();
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("Re-resolve and replay the current media");
          ImGui.SameLine();

          // DMCA
          if (ImGui.Button("DMCA", wideBtnSize)) {
              string url = _plugin.LastStreamURL;
              if (!string.IsNullOrEmpty(url)) {
                  string domain = "the site administrator";
                  try {
                      Uri uri = new Uri(url);
                      domain = uri.Host;
                  } catch { }
                  
                  string dmcaText = $"Content URL: {url}\n\nPlease contact {domain} to report this content.";
                  ImGui.SetClipboardText(dmcaText);
                  _plugin.Chat.Print("[Media Player] DMCA contact info and URL copied to clipboard.");
              } else {
                  _plugin.Chat.PrintError("[Media Player] No active media URL to copy.");
              }
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy DMCA info & media URL to clipboard");
          ImGui.SameLine();

          // Kill
          ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.7f, 0.15f, 0.15f, 1f));
          ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.9f, 0.2f, 0.2f, 1f));
          if (ImGui.Button("Kill", wideBtnSize)) {
            _plugin.RequestKillAndRestart();
          }
          ImGui.PopStyleColor(2);
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("Kill the media pipeline and restart it");
        }

        //  Volume Slider 
        if (_mediaManager != null) {
          ImGui.SetNextItemWidth(-(ImGui.CalcTextSize("Volume").X + ImGui.GetStyle().ItemInnerSpacing.X));
          int vol = (int)(_mediaManager.LiveStreamVolume * 100f);
          if (ImGui.SliderInt("Volume", ref vol, 0, 300)) {
              _mediaManager.LiveStreamVolume = vol / 100f;
          }
        }
        
        //  Emulation Controller 
        if (_plugin.CurrentStreamer == "Emulation" && _plugin.ControllerService != null) {
          ImGui.Separator();
          ImGui.SetNextItemWidth(100f);
          int comboIdx = _plugin.ControllerService.PlayerSlot == 255 ? 4 : _plugin.ControllerService.PlayerSlot;
          if (ImGui.Combo("Controller Slot", ref comboIdx, "Player 1\0Player 2\0Player 3\0Player 4\0None\0")) {
              _plugin.ControllerService.PlayerSlot = comboIdx == 4 ? (byte)255 : (byte)comboIdx;
          }
        }

        if (eventTriggerCooldown.ElapsedMilliseconds > 10000) {
          CheckWindowSize(true);
          eventTriggerCooldown.Restart();
          _lastWindowSize = Size;
        }
      }
    }

    private static string FormatTimeCode(long ms) {
      var ts = TimeSpan.FromMilliseconds(ms);
      return ts.Hours > 0 
        ? string.Format("{0}:{1:D2}:{2:D2}", ts.Hours, ts.Minutes, ts.Seconds)
        : string.Format("{0}:{1:D2}", ts.Minutes, ts.Seconds);
    }

    public void CheckWindowSize(bool triggerEvent) {
      if (_lastWindowSize != null) {
        if (_lastWindowSize.Value.X != Size.Value.X || _wasNotOpen) {
          if (IsOpen || (_plugin.WorldRenderer?.Transform.Enabled ?? false)) {
            if (IsOpen) {
              if (Size.Value.X < 360) {
                FeedType = TwitchFeedType._160p;
              }
              if (Size.Value.X >= 360 || Size.Value.X < 480) {
                FeedType = TwitchFeedType._360p;
              }
              if (Size.Value.X >= 480 || Size.Value.X < 720) {
                FeedType = TwitchFeedType._480p;
              }
              if (Size.Value.X >= 720 || Size.Value.X < 1080) {
                FeedType = TwitchFeedType._720p;
              }
              if (Size.Value.X >= 1080) {
                FeedType = TwitchFeedType._1080p;
              }
            } else {
              // Fallback to 720p when 2D window is hidden but 3D is active.
              FeedType = TwitchFeedType._720p;
            }
          } else {
            FeedType = TwitchFeedType.Audio;
            _wasNotOpen = true;
          }
          if (triggerEvent) {
            WindowResized?.Invoke(this, EventArgs.Empty);
          }
        }
      }
    }

    public void MarkDisposed() {
      lock (_textureLock) {
        _disposed = true;
        _videoTexture?.Dispose();
        _videoTexture = null;
        _blackFrame?.Dispose();
        _blackFrame = null;
      }
    }

    /// <summary>
    /// Returns the current video frame texture pointer and dimensions,
    /// or a black 16:9 placeholder if no video is playing.
    /// </summary>
    public void GetCurrentVideoTexture(out IntPtr srv, out int width, out int height, out int trueWidth, out int trueHeight) {
        trueWidth = 0;
        trueHeight = 0;
      lock (_textureLock) {
        if (_videoTexture != null && _videoTexture.ImGuiHandle != IntPtr.Zero) {
            srv = _videoTexture.ImGuiHandle;
            width = _videoTexture.Width;
            height = _videoTexture.Height;
            trueWidth = _videoTexture.TrueWidth;
            trueHeight = _videoTexture.TrueHeight;
        } else if (_blackFrame != null) {
            var handle = _blackFrame.Handle;
            srv = System.Runtime.CompilerServices.Unsafe.As<Dalamud.Bindings.ImGui.ImTextureID, IntPtr>(ref handle);
            width = _blackFrame.Width;
            height = _blackFrame.Height;
        } else {
            srv = IntPtr.Zero;
            width = 16;
            height = 9;
        }
      }

      // Prefer live frame metadata over cached texture fields.
      if (_mediaManager != null && (trueWidth <= 0 || trueHeight <= 0)) {
          lock (_mediaManager.FrameLock) {
              if (_mediaManager.LastFrameTrueWidth > 0) trueWidth = _mediaManager.LastFrameTrueWidth;
              if (_mediaManager.LastFrameTrueHeight > 0) trueHeight = _mediaManager.LastFrameTrueHeight;
          }
      }
    }

    /// <summary>
    /// Creates a small black 16:9 texture as a placeholder.
    /// </summary>
    private void CreateBlackFrame() {
      try {
        int w = 16, h = 9;
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) {
          pixels[i * 4 + 3] = 255;
        }
        _blackFrame = _textureProvider.CreateFromRaw(
          new Dalamud.Interface.Textures.RawImageSpecification(w, h, 28), pixels);
      } catch (Exception e) {
        _pluginLog.Warning(e, "[Media Player] Failed to create black placeholder texture.");
      }
    }

    /// <summary>
    /// Returns the pixel dimensions of the current video frame texture.
    /// </summary>
    public Vector2 GetCurrentTextureSize() {
      if (_videoTexture != null) {
        return new Vector2(_videoTexture.Width, _videoTexture.Height);
      }
      return Vector2.Zero;
    }

    private void DrawLoadingOverlay(Vector2 topLeft, Vector2 size) {
      if (_plugin == null || size.X <= 0 || size.Y <= 0) return;

      var drawList = ImGui.GetWindowDrawList();
      var bottomRight = topLeft + size;

      drawList.AddRectFilled(topLeft, bottomRight, 0xD0000000);

      string message = _plugin.MediaLoadingMessage;
      if (string.IsNullOrWhiteSpace(message)) {
        message = "Loading video...";
      }

      var titleSize = ImGui.CalcTextSize("Loading video...");
      float barWidth = Math.Min(size.X * 0.65f, 320f);
      float barHeight = 6f;
      float blockHeight = titleSize.Y + 16f + barHeight;
      float startY = topLeft.Y + (size.Y - blockHeight) * 0.5f;
      float centerX = topLeft.X + size.X * 0.5f;

      var msgSize = ImGui.CalcTextSize(message);
      if (msgSize.X > size.X - 24f) {
        message = "Buffering video...";
        msgSize = ImGui.CalcTextSize(message);
      }

      drawList.AddText(
        new Vector2(centerX - msgSize.X * 0.5f, startY),
        0xFFFFFFFF,
        message);

      float pulse = _plugin.MediaLoadingPulse;
      float barLeft = centerX - barWidth * 0.5f;
      float barTop = startY + titleSize.Y + 16f;
      var barMin = new Vector2(barLeft, barTop);
      var barMax = new Vector2(barLeft + barWidth, barTop + barHeight);
      drawList.AddRectFilled(barMin, barMax, 0x55FFFFFF);

      float segmentWidth = barWidth * 0.35f;
      float travel = barWidth - segmentWidth;
      float segLeft = barLeft + travel * pulse;
      drawList.AddRectFilled(
        new Vector2(segLeft, barTop),
        new Vector2(segLeft + segmentWidth, barTop + barHeight),
        0xFF4FC3F7);
    }
  }
}





