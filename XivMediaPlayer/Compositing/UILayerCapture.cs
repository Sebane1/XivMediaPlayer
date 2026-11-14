using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace XivMediaPlayer.Compositing {
  /// <summary>
  /// Captures the game's back buffer (which contains Scene + UI) at the start
  /// of each ImGui frame. After the video is blitted to the back buffer,
  /// RestoreUIRegions() copies the original UI addon rectangles back on top.
  /// </summary>
  internal unsafe class UILayerCapture : IDisposable {
    private ID3D11DeviceContext _context;
    private ID3D11Device _device;
    private ID3D11Texture2D _backBufferCopy;
    private ID3D11ShaderResourceView _backBufferSRV;
    private ID3D11Texture2D _previewStagingTexture;
    private int _width, _height;
    private Vortice.DXGI.Format _format;
    private bool _disposed;
    private bool _initialized;
    private bool _frameCaptured;
    private bool _preUiCapturedThisFrame;
    private ulong _lastPreDrawFrame;
    private Dalamud.Plugin.Services.IAddonLifecycle _addonLifecycle;
    private string _debugInfo = "Not initialized";

    public UILayerCapture(Dalamud.Plugin.Services.IAddonLifecycle addonLifecycle) {
        _addonLifecycle = addonLifecycle;
    }

    public string DebugInfo => _debugInfo;
    public bool IsInitialized => _initialized;
    public ID3D11ShaderResourceView BackBufferSRV => _backBufferSRV;
    public byte[] LastAlphaData { get; private set; }
    public byte[] LastColorData { get; private set; }
    public int CaptureWidth { get; private set; }
    public int CaptureHeight { get; private set; }
    public int Width => _width;
    public int Height => _height;
    public bool Initialize() {
      if (_initialized || _disposed) return _initialized;

      try {
        var ffxivDevice = Device.Instance();
        if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null) {
          _debugInfo = "FFXIV D3D11 device context not available.";
          return false;
        }

        var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
        Marshal.AddRef(contextPtr);
        _context = new ID3D11DeviceContext(contextPtr);
        _device = _context.Device;

        
        _initialized = true;
        _debugInfo = "Initialized, waiting for first capture.";
        return true;
      } catch (Exception ex) {
        _debugInfo = $"Init failed: {ex.Message}";
        return false;
      }
    }

    private bool CaptureToTexture(ref ID3D11Texture2D targetCopy, ref ID3D11ShaderResourceView targetSrv, bool isPreUI) {
      try {
        var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
        if (rtm == null || rtm->SwapChainBackBuffer == null || rtm->SwapChainBackBuffer->D3D11Texture2D == null) {
          _debugInfo = "RTM SwapChainBackBuffer not available";
          return false;
        }

        var texPtr = (IntPtr)rtm->SwapChainBackBuffer->D3D11Texture2D;
        System.Runtime.InteropServices.Marshal.AddRef(texPtr);
        var backBuffer = new Vortice.Direct3D11.ID3D11Texture2D(texPtr);
        var desc = backBuffer.Description;

        if (targetCopy == null || _width != (int)desc.Width || _height != (int)desc.Height || _format != desc.Format) {
          targetCopy?.Dispose();
          targetSrv?.Dispose();
          targetSrv = null;
          
          if (!isPreUI) {
             _previewStagingTexture?.Dispose();
             _previewStagingTexture = null;
             DisposeProbeRing();
             _width = (int)desc.Width;
             _height = (int)desc.Height;
             _format = desc.Format;
          }

          targetCopy = _device.CreateTexture2D(new Texture2DDescription {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
          });

          targetSrv = _device.CreateShaderResourceView(targetCopy);

          if (!isPreUI) {
              _previewStagingTexture = _device.CreateTexture2D(new Texture2DDescription {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
              });
          }
        }

        if (desc.SampleDescription.Count > 1) {
          _context.ResolveSubresource(targetCopy, 0, backBuffer, 0, desc.Format);
        } else {
          _context.CopyResource(targetCopy, backBuffer);
        }

        backBuffer.Dispose();
        return true;
      } catch (Exception ex) {
        _debugInfo = $"Capture failed: {ex.Message}";
        return false;
      }
    }



    /// <summary>
    /// Call at the very start of OnDraw, before any ImGui rendering.
    /// Captures the current back buffer (Scene + Game UI). The alpha channel contains the UI mask!
    /// </summary>
    public void CaptureFrame() {
      if (_disposed || !_initialized) return;
      _frameCaptured = false;
      _preUiCapturedThisFrame = false;

      if (CaptureToTexture(ref _backBufferCopy, ref _backBufferSRV, false)) {
        _frameCaptured = true;
        _debugInfo = $"Captured {_width}x{_height}";

        // Also enumerate visible addons for debug
        LastAddonRects = GetVisibleAddonRects();
        _debugInfo = $"Captured {_width}x{_height}, {LastAddonRects.Count} addons";
      }
    }

    // Async 1x1 readback ring for IsPixelOccluding. Mapping a staging texture right after the
    // copy stalls the CPU until the GPU finishes the frame, so we queue the copy and read it
    // back a frame or two later instead.
    private const int ProbeRingSize = 3;
    private sealed class ProbeSlot {
      public ID3D11Texture2D BackBufferStaging;
      public ID3D11Texture2D Unk68Staging;
      public bool Pending;
      public bool HasUnk68;
      public float GameDepth;
      public bool UseDifferenceFallback;
    }
    private readonly ProbeSlot[] _probeRing = new ProbeSlot[ProbeRingSize];
    private int _probeWriteIndex;
    private bool _lastProbeOccluding;

    /// <summary>
    /// Determines whether the game UI is occluding this back-buffer pixel.
    /// Uses the same difference math as the shader to ignore modded skyboxes and handle translucent UI.
    /// The answer is non-blocking and lags the cursor by 1-2 frames.
    /// </summary>
    public bool IsPixelOccluding(int x, int y, IntPtr unk68Ptr, DepthBufferCapture depthCapture, bool useDifferenceFallback) {
      if (_disposed || !_initialized || !_frameCaptured || _backBufferCopy == null) return false;
      if (x < 0 || y < 0 || x >= _width || y >= _height) return false;

      try {
        // 1. Resolve finished probes, oldest first, without waiting on the GPU.
        for (int i = 0; i < ProbeRingSize; i++) {
          var slot = _probeRing[(_probeWriteIndex + i) % ProbeRingSize];
          if (slot == null || !slot.Pending) continue;
          if (!TryResolveProbe(slot, out bool occluding)) break;
          _lastProbeOccluding = occluding;
        }

        // 2. Queue a probe for this frame's cursor position.
        var writeSlot = _probeRing[_probeWriteIndex] ??= new ProbeSlot();
        if (!writeSlot.Pending) {
          if (writeSlot.BackBufferStaging == null || writeSlot.BackBufferStaging.Description.Format != _format) {
            writeSlot.BackBufferStaging?.Dispose();
            writeSlot.BackBufferStaging = CreateStaging1x1(_format);
          }
          var box = new Vortice.Mathematics.Box(x, y, 0, x + 1, y + 1, 1);
          _context.CopySubresourceRegion(writeSlot.BackBufferStaging, 0, 0, 0, 0, _backBufferCopy, 0, box);

          writeSlot.HasUnk68 = false;
          if (unk68Ptr != IntPtr.Zero && depthCapture != null) {
            System.Runtime.InteropServices.Marshal.AddRef(unk68Ptr);
            using var unk68Srv = new ID3D11ShaderResourceView(unk68Ptr);
            using var unk68Tex = unk68Srv.Resource.QueryInterface<ID3D11Texture2D>();
            var unkDesc = unk68Tex.Description;
            if (x < unkDesc.Width && y < unkDesc.Height) {
              if (writeSlot.Unk68Staging == null || writeSlot.Unk68Staging.Description.Format != unkDesc.Format) {
                writeSlot.Unk68Staging?.Dispose();
                writeSlot.Unk68Staging = CreateStaging1x1(unkDesc.Format);
              }
              _context.CopySubresourceRegion(writeSlot.Unk68Staging, 0, 0, 0, 0, unk68Tex, 0, box);

              // Depth buffer may be at a different (render) resolution than the back buffer.
              int dx = depthCapture.DepthWidth > 0 ? (int)((long)x * depthCapture.DepthWidth / _width) : x;
              int dy = depthCapture.DepthHeight > 0 ? (int)((long)y * depthCapture.DepthHeight / _height) : y;
              writeSlot.GameDepth = depthCapture.GetDepthAt(dx, dy);
              writeSlot.HasUnk68 = true;
            }
          }
          writeSlot.UseDifferenceFallback = useDifferenceFallback;
          writeSlot.Pending = true;
          _probeWriteIndex = (_probeWriteIndex + 1) % ProbeRingSize;
        }

        return _lastProbeOccluding;
      } catch {
        return false;
      }
    }

    private void DisposeProbeRing() {
      for (int i = 0; i < ProbeRingSize; i++) {
        _probeRing[i]?.BackBufferStaging?.Dispose();
        _probeRing[i]?.Unk68Staging?.Dispose();
        _probeRing[i] = null;
      }
      _probeWriteIndex = 0;
      _lastProbeOccluding = false;
    }

    private ID3D11Texture2D CreateStaging1x1(Vortice.DXGI.Format format) {
      return _device.CreateTexture2D(new Texture2DDescription {
        Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
        Format = format, SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
      });
    }

    private bool TryResolveProbe(ProbeSlot slot, out bool occluding) {
      occluding = false;
      var hr = _context.Map(slot.BackBufferStaging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out MappedSubresource mapped);
      if (hr.Failure) return false;

      float nativeAlpha, bR, bG, bB;
      unsafe {
        byte* ptr = (byte*)mapped.DataPointer;
        bB = ptr[0] / 255f;
        bG = ptr[1] / 255f;
        bR = ptr[2] / 255f;
        nativeAlpha = ptr[3] / 255f;
      }
      _context.Unmap(slot.BackBufferStaging, 0);

      float uR = 0f, uG = 0f, uB = 0f, uA = 0f;
      if (slot.HasUnk68 && slot.Unk68Staging != null) {
        // Copied in the same frame as the back-buffer pixel, so it is ready too.
        var hr2 = _context.Map(slot.Unk68Staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out MappedSubresource mappedUnk68);
        if (hr2.Failure) return false;
        unsafe {
          if (slot.Unk68Staging.Description.Format == Vortice.DXGI.Format.R16G16B16A16_Float) {
            ushort* ptr16 = (ushort*)mappedUnk68.DataPointer;
            uR = (float)BitConverter.Int16BitsToHalf((short)ptr16[0]);
            uG = (float)BitConverter.Int16BitsToHalf((short)ptr16[1]);
            uB = (float)BitConverter.Int16BitsToHalf((short)ptr16[2]);
            uA = (float)BitConverter.Int16BitsToHalf((short)ptr16[3]);
          } else {
            byte* ptr = (byte*)mappedUnk68.DataPointer;
            uR = ptr[0] / 255f;
            uG = ptr[1] / 255f;
            uB = ptr[2] / 255f;
            uA = ptr[3] / 255f;
          }
        }
        _context.Unmap(slot.Unk68Staging, 0);
      }
      slot.Pending = false;
      occluding = ComputeOcclusion(nativeAlpha, bR, bG, bB, slot.HasUnk68, uR, uG, uB, uA, slot.GameDepth, slot.UseDifferenceFallback);
      return true;
    }

    private static bool ComputeOcclusion(float nativeAlpha, float bR, float bG, float bB, bool hasUnk68,
        float uR, float uG, float uB, float uA, float gameDepth, bool useDifferenceFallback) {
        float trueAlpha = nativeAlpha;

        if (hasUnk68) {
            bool isSkybox = (gameDepth < 0.00001f);

            // Estimated Alpha Math
            float estR = (bR > uR) ? (bR - uR) / Math.Max(0.0001f, 1.0f - uR) : 1.0f - (bR / Math.Max(0.0001f, uR));
            float estG = (bG > uG) ? (bG - uG) / Math.Max(0.0001f, 1.0f - uG) : 1.0f - (bG / Math.Max(0.0001f, uG));
            float estB = (bB > uB) ? (bB - uB) / Math.Max(0.0001f, 1.0f - uB) : 1.0f - (bB / Math.Max(0.0001f, uB));
            float estimatedAlpha = Math.Clamp(Math.Max(Math.Max(estR, estG), estB), 0f, 1f);
            
            float diffMax2 = Math.Max(Math.Max(Math.Abs(bR - uR), Math.Abs(bG - uG)), Math.Abs(bB - uB));
            float alphaDiff = Math.Abs(nativeAlpha - uA);

            if (isSkybox) {
                if (useDifferenceFallback) {
                    trueAlpha = (diffMax2 > 0.02f) ? estimatedAlpha : 0.0f;
                } else {
                    trueAlpha = nativeAlpha;
                }
            } else {
                trueAlpha = Math.Clamp(Math.Max(estimatedAlpha, alphaDiff), 0f, 1f);
            }
        }

        return trueAlpha > 0.1f;
    }

    /// <summary>
    /// After the video has been blitted to the back buffer, this copies
    /// the original UI addon regions from the saved frame back on top.
    /// This pixel-perfectly restores game UI that was overwritten by the video.
    /// </summary>
    public void RestoreUIRegions() {
      if (_disposed || !_initialized || !_frameCaptured || _backBufferCopy == null) return;

      try {
        // Get the current back buffer
        var savedRTVs = new ID3D11RenderTargetView[1];
        ID3D11DepthStencilView savedDSV;
        _context.OMGetRenderTargets(1, savedRTVs, out savedDSV);

        if (savedRTVs[0] == null) {
          _debugInfo = "RestoreUI: No RTV bound";
          return;
        }

        using var rtvResource = savedRTVs[0].Resource;
        using var backBuffer = rtvResource.QueryInterface<ID3D11Texture2D>();

        // Use the addon rects already enumerated during CaptureFrame
        int restored = 0;

        foreach (var rect in LastAddonRects) {
          // Clamp to screen bounds
          int x = Math.Max(0, rect.X);
          int y = Math.Max(0, rect.Y);
          int right = Math.Min(_width, rect.X + rect.W);
          int bottom = Math.Min(_height, rect.Y + rect.H);

          if (right <= x || bottom <= y) continue;

          // CopySubresourceRegion: copy this rectangle from saved frame to back buffer
          var srcBox = new Vortice.Mathematics.Box(x, y, 0, right, bottom, 1);
          _context.CopySubresourceRegion(backBuffer, 0, x, y, 0, _backBufferCopy, 0, srcBox);
          restored++;
        }

        _debugInfo = $"Restored {restored}/{LastAddonRects.Count} UI regions";
      } catch (Exception ex) {
        _debugInfo = $"Restore error: {ex.Message}";
      }
    }

    /// <summary>
    /// The last set of addon rectangles found. Exposed for debug visualization.
    /// </summary>
    public List<(int X, int Y, int W, int H, string Name)> LastAddonRects { get; private set; }
      = new List<(int X, int Y, int W, int H, string Name)>();

    /// <summary>
    /// Enumerates all visible ATK addon units and returns their screen rectangles.
    /// </summary>
    private List<(int X, int Y, int W, int H, string Name)> GetVisibleAddonRects() {
      var rects = new List<(int X, int Y, int W, int H, string Name)>();

      try {
        var unitManager = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance();
        if (unitManager == null) {
          _debugInfo = "RaptureAtkUnitManager is null";
          return rects;
        }

        // Iterate through the AtkUnitList entries
        var unitList = &unitManager->AllLoadedUnitsList;
        for (int i = 0; i < unitList->Count; i++) {
          var unitBase = unitList->Entries[i].Value;
          if (unitBase == null) continue;

          // Only include visible addons with nonzero size
          if (!unitBase->IsVisible || unitBase->RootNode == null) continue;

          var x = (int)unitBase->X;
          var y = (int)unitBase->Y;
          var w = (int)(unitBase->RootNode->Width * unitBase->Scale);
          var h = (int)(unitBase->RootNode->Height * unitBase->Scale);
          var name = unitBase->NameString;

          if (w <= 0 || h <= 0) continue;

          rects.Add((x, y, w, h, name));
        }
      } catch (Exception ex) {
        _debugInfo = $"Addon enum error: {ex.Message}";
      }

      return rects;
    }

    public void GeneratePreview() {
      if (_disposed || !_initialized || !_frameCaptured || _backBufferCopy == null || _previewStagingTexture == null) return;
      try {
        _context.CopyResource(_previewStagingTexture, _backBufferCopy);
        var mapped = _context.Map(_previewStagingTexture, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try {
          int captureW = Math.Min(480, _width);
          int captureH = Math.Min(270, _height);
          int stepX = _width / captureW;
          int stepY = _height / captureH;
          CaptureWidth = captureW;
          CaptureHeight = captureH;

          if (LastAlphaData == null || LastAlphaData.Length != captureW * captureH * 4) {
            LastAlphaData = new byte[captureW * captureH * 4];
          }
          if (LastColorData == null || LastColorData.Length != captureW * captureH * 4) {
            LastColorData = new byte[captureW * captureH * 4];
          }

          unsafe {
            byte* ptr = (byte*)mapped.DataPointer;
            for (int y = 0; y < captureH; y++) {
              for (int x = 0; x < captureW; x++) {
                int srcX = x * stepX;
                int srcY = y * stepY;
                byte* pixel = ptr + srcY * mapped.RowPitch + srcX * 4;
                
                byte b = pixel[0];
                byte g = pixel[1];
                byte r = pixel[2];
                byte alpha = pixel[3]; // FFXIV backbuffer is usually B8G8R8A8, so 3 is A
                int idx = (y * captureW + x) * 4;
                LastAlphaData[idx + 0] = alpha;
                LastAlphaData[idx + 1] = alpha;
                LastAlphaData[idx + 2] = alpha;
                LastAlphaData[idx + 3] = 255;
                
                LastColorData[idx + 0] = r;
                LastColorData[idx + 1] = g;
                LastColorData[idx + 2] = b;
                LastColorData[idx + 3] = 255;
              }
            }
          }
        } finally {
          _context.Unmap(_previewStagingTexture, 0);
        }
      } catch (Exception ex) {
        _debugInfo = $"UI Preview error: {ex.Message}";
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;
      DisposeProbeRing();
      _previewStagingTexture?.Dispose();
      _backBufferSRV?.Dispose();
      _backBufferCopy?.Dispose();
      _context?.Dispose();
      _device?.Dispose();
      _device = null;
      _context = null;
    }
  }
}


