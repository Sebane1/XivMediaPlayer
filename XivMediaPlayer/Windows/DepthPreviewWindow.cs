using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;
using XivMediaPlayer.Compositing;
using XivMediaPlayer.Localization;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using Vortice.Direct3D11;

namespace XivMediaPlayer.Windows {
  /// <summary>
  /// Debug window that shows the game's depth buffer as a grayscale image.
  /// White = near (depth 1.0 in reverse-Z), Black = far (depth 0.0).
  /// </summary>
  internal unsafe class DepthPreviewWindow : Window, IDisposable {
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;
    
    private SceneReconstructionPreviewRenderer _sceneRenderer;
    private readonly ITextureProvider _textureProvider;
    private readonly IPluginLog _pluginLog;
    private IDalamudTextureWrap _previewTexture;
    private IDalamudTextureWrap _uiPreviewTexture;
    private int _lastDataHash;
    private int _lastUiDataHash;
    private bool _disposed;
    private ID3D11Texture2D _dynamicCopyTex;
    
    private int _selectedPreviewMode = 0;
    private static readonly string[] PreviewModeKeys =
    {
        "Depth Buffer (Raw Capture)",
        "UI BackBuffer (Raw Capture)",
        "RTM: DepthStencil",
        "RTM: GBuffer 0 (Normal)",
        "RTM: GBuffer 1",
        "RTM: GBuffer 2 (Diffuse)",
        "RTM: GBuffer 3",
        "RTM: GBuffer 4",
        "RTM: SemiTransparent GBuffer 0",
        "RTM: SemiTransparent GBuffer 1",
        "RTM: SemiTransparent GBuffer 2",
        "RTM: SemiTransparent GBuffer 3",
        "RTM: SemiTransparent GBuffer 4",
        "RTM: Tone Adjust Source",
        "RTM: Shadow",
        "RTM: LightDiffuse",
        "RTM: LightSpecular",
        "RTM: SwapChainBackBuffer",
        "RTM: SwapChainDepthStencil",
        "Reconstructed Scene (GBuffer2 * GBuffer3)"
    };

    private string L(string text) => Translation.Get(text);

    private ID3D11ShaderResourceView _dynamicSrv;
    private IntPtr _dynamicSrvTexPtr;
    private XivMediaPlayer.Utils.TextureDumper _dumper;

    /// <summary>
    /// The shared depth capture instance. Initialized externally.
    /// </summary>
    public DepthBufferCapture Capture { get; set; }
    public UILayerCapture UICapture { get; set; }
    public Configuration Config { get; set; }

    public DepthPreviewWindow(ITextureProvider textureProvider, IPluginLog pluginLog)
      : base("Depth Buffer Preview", ImGuiWindowFlags.None, false) {
      _textureProvider = textureProvider;
      _pluginLog = pluginLog;
      Size = new Vector2(640, 400);
      SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override unsafe void Draw() {
      if (_disposed) return;

      WindowName = L("Depth Buffer Preview");
      string[] previewModes = Array.ConvertAll(PreviewModeKeys, L);
      ImGui.Combo(L("Preview Mode"), ref _selectedPreviewMode, previewModes, previewModes.Length);
      ImGui.SameLine();
      if (ImGui.Button(L("Copy to Clipboard"))) {
          CopyToClipboard();
      }
      ImGui.SameLine();
      if (ImGui.Button(L("Export Preview Frame to Desktop"))) {
          ExportPreviewFrame();
      }
      ImGui.SameLine();
      if (ImGui.Button(L("Dump RTM Fields to Log"))) {
          DumpRtmFields();
      }
      ImGui.SameLine();
      if (ImGui.Button(L("Bulk Export Buffers to Desktop"))) {
          BulkExportBuffers();
      }
      ImGui.Separator();

      if (_selectedPreviewMode == 0 || _selectedPreviewMode == 1) {
          DrawStandardPreviews();
          return;
      }

      var rtm = RenderTargetManager.Instance();
      if (rtm == null) {
        ImGui.TextColored(new Vector4(1, 0, 0, 1), L("RenderTargetManager is null"));
        return;
      }

      if (_selectedPreviewMode == 19) {
          if (_sceneRenderer == null) {
              _sceneRenderer = new SceneReconstructionPreviewRenderer();
              _sceneRenderer.Initialize();
          }
          
          if (rtm->GBuffers[0].Value != null && rtm->GBuffers[1].Value != null && rtm->GBuffers[2].Value != null && rtm->GBuffers[3].Value != null && rtm->GBuffers[4].Value != null && UICapture?.BackBufferSRV != null) {
              var lightDiffuse = *(Texture**)((byte*)rtm + 0x58);
              var lightSpecular = *(Texture**)((byte*)rtm + 0x60);
              var unk68 = *(Texture**)((byte*)rtm + 0x68);

              if (lightDiffuse != null && lightSpecular != null && unk68 != null) {
                  _sceneRenderer.Update(rtm->GBuffers[0].Value, rtm->GBuffers[1].Value, rtm->GBuffers[2].Value, rtm->GBuffers[3].Value, rtm->GBuffers[4].Value, unk68, lightDiffuse, lightSpecular, UICapture.BackBufferSRV);
              }
          }
          
          int showMode = _sceneRenderer.ShowMode;
          if (ImGui.RadioButton(L("Reconstructed Scene"), showMode == 0)) { _sceneRenderer.ShowMode = 0; }
          ImGui.SameLine();
          if (ImGui.RadioButton(L("Difference Map"), showMode == 1)) { _sceneRenderer.ShowMode = 1; }
          ImGui.SameLine();
          if (ImGui.RadioButton(L("Alpha Mask"), showMode == 2)) { _sceneRenderer.ShowMode = 2; }
          ImGui.SameLine();
          if (ImGui.RadioButton(L("Inverted Difference"), showMode == 3)) { _sceneRenderer.ShowMode = 3; }
          ImGui.SameLine();
          if (ImGui.RadioButton(L("Native SwapChain Alpha"), showMode == 4)) { _sceneRenderer.ShowMode = 4; }
          ImGui.SameLine();
          if (ImGui.RadioButton(L("Alpha Difference"), showMode == 5)) { _sceneRenderer.ShowMode = 5; }
          ImGui.SameLine();
          if (ImGui.RadioButton(L("UI Extraction (Colored)"), showMode == 6)) { _sceneRenderer.ShowMode = 6; }
          
          if (_sceneRenderer.PreviewTextureHandle != IntPtr.Zero) {
              var avail = ImGui.GetContentRegionAvail();
              float drawH = avail.Y;
              float drawW = drawH * (16.0f / 9.0f); // Default wide aspect
              if (drawW > avail.X) {
                drawW = avail.X;
                drawH = drawW / (16.0f / 9.0f);
              }

              var srvHandle = _sceneRenderer.PreviewTextureHandle;
              var textureId = System.Runtime.CompilerServices.Unsafe.As<IntPtr, Dalamud.Bindings.ImGui.ImTextureID>(ref srvHandle);
              ImGui.Image(textureId, new Vector2(drawW, drawH));
          } else {
              ImGui.TextColored(new Vector4(1, 0, 0, 1), L("Failed to render reconstructed scene."));
          }
          return;
      }

      Texture* tex = null;
      switch (_selectedPreviewMode) {
        case 2: tex = rtm->DepthStencil; break;
        case 3: tex = rtm->GBuffers[0].Value; break;
        case 4: tex = rtm->GBuffers[1].Value; break;
        case 5: tex = rtm->GBuffers[2].Value; break;
        case 6: tex = rtm->GBuffers[3].Value; break;
        case 7: tex = rtm->GBuffers[4].Value; break;
        case 8: tex = rtm->SemitransparentGBuffers[0].Value; break;
        case 9: tex = rtm->SemitransparentGBuffers[1].Value; break;
        case 10: tex = rtm->SemitransparentGBuffers[2].Value; break;
        case 11: tex = rtm->SemitransparentGBuffers[3].Value; break;
        case 12: tex = rtm->SemitransparentGBuffers[4].Value; break;
        case 13: tex = rtm->ToneAdjustSource; break;
        case 14: tex = *(Texture**)((byte*)rtm + 0x50); break; // Shadow
        case 15: tex = *(Texture**)((byte*)rtm + 0x58); break; // LightDiffuse
        case 16: tex = *(Texture**)((byte*)rtm + 0x60); break; // LightSpecular
        case 17: tex = rtm->SwapChainBackBuffer; break;
        case 18: tex = rtm->SwapChainDepthStencil; break;
      }

      if (tex == null || tex->D3D11Texture2D == null) {
        ImGui.TextColored(new Vector4(1, 1, 0, 1), L("Selected texture is null or not initialized."));
        return;
      }

      var srv = GetOrCreateSRV(tex);
      if (srv == null) {
        ImGui.TextColored(new Vector4(1, 0, 0, 1), L("Failed to create ShaderResourceView for this texture."));
        return;
      }

      var availMode = ImGui.GetContentRegionAvail();
      var texPtr = (IntPtr)tex->D3D11Texture2D;
      System.Runtime.InteropServices.Marshal.AddRef(texPtr);
      using var d3dTex = new Vortice.Direct3D11.ID3D11Texture2D(texPtr);
      float aspect = (float)d3dTex.Description.Width / d3dTex.Description.Height;
      float drawHMode = availMode.Y;
      float drawWMode = drawHMode * aspect;
      if (drawWMode > availMode.X) {
        drawWMode = availMode.X;
        drawHMode = drawWMode / aspect;
      }

      var srvHandleMode = srv.NativePointer;
      var textureIdMode = System.Runtime.CompilerServices.Unsafe.As<IntPtr, Dalamud.Bindings.ImGui.ImTextureID>(ref srvHandleMode);
      ImGui.Image(textureIdMode, new Vector2(drawWMode, drawHMode));
    }

    private unsafe ID3D11ShaderResourceView GetOrCreateSRV(Texture* tex) {
        if (tex == null || tex->D3D11Texture2D == null) return null;
        var texPtr = (IntPtr)tex->D3D11Texture2D;
        if (_dynamicSrvTexPtr == texPtr && _dynamicSrv != null) {
            if (_dynamicCopyTex != null) {
                System.Runtime.InteropServices.Marshal.AddRef(texPtr);
                using var d3dTex2 = new Vortice.Direct3D11.ID3D11Texture2D(texPtr);
                using var device2 = d3dTex2.Device;
                using var context2 = device2.ImmediateContext;
                if (d3dTex2.Description.SampleDescription.Count > 1) {
                    context2.ResolveSubresource(_dynamicCopyTex, 0, d3dTex2, 0, d3dTex2.Description.Format);
                } else {
                    context2.CopyResource(_dynamicCopyTex, d3dTex2);
                }
            }
            return _dynamicSrv;
        }
        
        _dynamicSrv?.Dispose();
        _dynamicSrv = null;
        _dynamicCopyTex?.Dispose();
        _dynamicCopyTex = null;
        _dynamicSrvTexPtr = texPtr;
        
        System.Runtime.InteropServices.Marshal.AddRef(texPtr);
        using var d3dTex = new Vortice.Direct3D11.ID3D11Texture2D(texPtr);
        try {
            var targetTex = d3dTex;
            
            if ((d3dTex.Description.BindFlags & BindFlags.ShaderResource) == 0) {
                using var device = d3dTex.Device;
                var desc = d3dTex.Description;
                desc.BindFlags = BindFlags.ShaderResource;
                desc.Usage = ResourceUsage.Default;
                desc.CPUAccessFlags = CpuAccessFlags.None;
                desc.MiscFlags = ResourceOptionFlags.None;
                
                _dynamicCopyTex = device.CreateTexture2D(desc);
                targetTex = _dynamicCopyTex;
                
                using var context = device.ImmediateContext;
                if (desc.SampleDescription.Count > 1) {
                    context.ResolveSubresource(_dynamicCopyTex, 0, d3dTex, 0, desc.Format);
                } else {
                    context.CopyResource(_dynamicCopyTex, d3dTex);
                }
            }

            using var targetDevice = targetTex.Device;
            // For depth buffers, we need a specific format
            if (targetTex.Description.Format == Vortice.DXGI.Format.R24G8_Typeless) {
                var desc = new ShaderResourceViewDescription {
                    Format = Vortice.DXGI.Format.R24_UNorm_X8_Typeless,
                    ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
                };
                _dynamicSrv = targetDevice.CreateShaderResourceView(targetTex, desc);
            } else if (targetTex.Description.Format == Vortice.DXGI.Format.R32_Typeless) {
                var desc = new ShaderResourceViewDescription {
                    Format = Vortice.DXGI.Format.R32_Float,
                    ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
                };
                _dynamicSrv = targetDevice.CreateShaderResourceView(targetTex, desc);
            } else if (targetTex.Description.Format.ToString().Contains("Typeless")) {
                Vortice.DXGI.Format newFmt = targetTex.Description.Format;
                if (newFmt == Vortice.DXGI.Format.R8G8B8A8_Typeless) newFmt = Vortice.DXGI.Format.R8G8B8A8_UNorm;
                else if (newFmt == Vortice.DXGI.Format.R16G16B16A16_Typeless) newFmt = Vortice.DXGI.Format.R16G16B16A16_Float;
                else if (newFmt == Vortice.DXGI.Format.R32G32B32A32_Typeless) newFmt = Vortice.DXGI.Format.R32G32B32A32_Float;
                else if (newFmt == Vortice.DXGI.Format.R10G10B10A2_Typeless) newFmt = Vortice.DXGI.Format.R10G10B10A2_UNorm;
                
                var desc = new ShaderResourceViewDescription {
                    Format = newFmt,
                    ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
                };
                _dynamicSrv = targetDevice.CreateShaderResourceView(targetTex, desc);
            } else {
                _dynamicSrv = targetDevice.CreateShaderResourceView(targetTex);
            }
        } catch (Exception ex) {
            _pluginLog.Warning(ex, "[Depth Preview] Failed to create SRV.");
        }
        
        return _dynamicSrv;
    }

    private void DrawStandardPreviews() {
      if (Capture != null) {
          Capture.ReadDepthEnabled = true;
          Capture.GeneratePreview();
      }
      var data = Capture?.LastRgbaData;
      if (data != null && data.Length > 0) {
        try {
          int hash = data.Length;
          int step = Math.Max(4, data.Length / 64);
          for (int i = 0; i < data.Length; i += step) {
            hash = hash * 31 + data[i];
          }

          if (hash != _lastDataHash) {
            var oldTex = _previewTexture;
            _previewTexture = _textureProvider.CreateFromRaw(
              new RawImageSpecification(Capture.CaptureWidth, Capture.CaptureHeight, 28), data);
            _lastDataHash = hash;
            oldTex?.Dispose();
          }
        } catch (Exception e) {
          _pluginLog.Warning(e, "[Depth Preview] Failed to create preview texture.");
        }

        if (_previewTexture != null) {
          var avail = ImGui.GetContentRegionAvail();
          // We want the image to fit half the window height to leave room for the UI preview
          float drawH = avail.Y * 0.5f; 
          float aspect = (float)Capture.CaptureWidth / Capture.CaptureHeight;
          float drawW = drawH * aspect;
          if (drawW > avail.X) {
            drawW = avail.X;
            drawH = drawW / aspect;
          }
          ImGui.Image(_previewTexture.Handle, new Vector2(drawW, drawH));
        }
      } else {
        ImGui.TextColored(new Vector4(1, 1, 0, 1), L("No depth data captured yet."));
      }

      // UI Capture debug section
      if (UICapture != null) {
        ImGui.Separator();
        UICapture.GeneratePreview();
        ImGui.TextColored(new Vector4(0, 1, 1, 1), string.Format(L("UI Capture: {0}"), UICapture.DebugInfo));
        
        var uiData = UICapture.LastAlphaData;
        
        if (uiData != null && uiData.Length > 0) {
          try {
            int hash = uiData.Length;
            int step = Math.Max(4, uiData.Length / 64);
            for (int i = 0; i < uiData.Length; i += step) {
              hash = hash * 31 + uiData[i];
            }

            if (hash != _lastUiDataHash) {
              var oldTex = _uiPreviewTexture;
              _uiPreviewTexture = _textureProvider.CreateFromRaw(
                new RawImageSpecification(UICapture.CaptureWidth, UICapture.CaptureHeight, 28), uiData);
              _lastUiDataHash = hash;
              oldTex?.Dispose();
            }
          } catch (Exception e) {
            _pluginLog.Warning(e, "[Depth Preview] Failed to create UI preview texture.");
          }

          if (_uiPreviewTexture != null) {
            var avail = ImGui.GetContentRegionAvail();
            float drawH = avail.Y; 
            float aspect = (float)UICapture.CaptureWidth / UICapture.CaptureHeight;
            float drawW = drawH * aspect;
            if (drawW > avail.X) {
              drawW = avail.X;
              drawH = drawW / aspect;
            }
            ImGui.Image(_uiPreviewTexture.Handle, new Vector2(drawW, drawH));
          }
        }

        ImGui.Text(string.Format(L("Addon rects: {0}"), UICapture.LastAddonRects.Count));
        if (ImGui.CollapsingHeader(L("Detected Addons"))) {
          for (int i = 0; i < UICapture.LastAddonRects.Count; i++) {
            var r = UICapture.LastAddonRects[i];
            ImGui.Text($"  [{i}] {r.Name}: ({r.X},{r.Y}) {r.W}x{r.H}");
          }
        }
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;
      _previewTexture?.Dispose();
      _uiPreviewTexture?.Dispose();
      _dynamicSrv?.Dispose();
      _dynamicCopyTex?.Dispose();
      _sceneRenderer?.Dispose();
      _dumper?.Dispose();
    }

    private unsafe void CopyToClipboard() {
        byte[] rgbaData = null;
        int w = 0, h = 0;

        try {
            if (_selectedPreviewMode == 0 && Capture != null) {
                rgbaData = Capture.LastRgbaData;
                w = Capture.CaptureWidth;
                h = Capture.CaptureHeight;
            } else if (_selectedPreviewMode == 1 && UICapture != null) {
                rgbaData = UICapture.LastColorData;
                w = UICapture.CaptureWidth;
                h = UICapture.CaptureHeight;
            } else if (_selectedPreviewMode == 19) {
                if (_sceneRenderer == null || !_sceneRenderer.IsInitialized || _sceneRenderer.PreviewSRV == null) {
                    _pluginLog.Warning("[Depth Preview] Scene renderer not initialized.");
                    return;
                }
                if (_dumper == null) {
                    _dumper = new XivMediaPlayer.Utils.TextureDumper();
                    _dumper.Initialize();
                }
                w = 1920;
                h = 1080;
                rgbaData = _dumper.DumpTextureToRgba(_sceneRenderer.PreviewSRV, w, h);
                
                // Force Alpha to 255 so the clipboard image isn't transparent.
                if (rgbaData != null) {
                    for (int i = 0; i < rgbaData.Length; i += 4) {
                        rgbaData[i + 3] = 255;
                    }
                }
            } else {
                var rtm = RenderTargetManager.Instance();
                if (rtm == null) return;

                Texture* tex = null;
                switch (_selectedPreviewMode) {
                  case 2: tex = rtm->DepthStencil; break;
                  case 3: tex = rtm->GBuffers[0].Value; break;
                  case 4: tex = rtm->GBuffers[1].Value; break;
                  case 5: tex = rtm->GBuffers[2].Value; break;
                  case 6: tex = rtm->GBuffers[3].Value; break;
                  case 7: tex = rtm->GBuffers[4].Value; break;
                  case 8: tex = rtm->SemitransparentGBuffers[0].Value; break;
                  case 9: tex = rtm->SemitransparentGBuffers[1].Value; break;
                  case 10: tex = rtm->SemitransparentGBuffers[2].Value; break;
                  case 11: tex = rtm->SemitransparentGBuffers[3].Value; break;
                  case 12: tex = rtm->SemitransparentGBuffers[4].Value; break;
                  case 13: tex = rtm->ToneAdjustSource; break;
                  case 14: tex = *(Texture**)((byte*)rtm + 0x50); break; // Shadow
                  case 15: tex = *(Texture**)((byte*)rtm + 0x58); break; // LightDiffuse
                  case 16: tex = *(Texture**)((byte*)rtm + 0x60); break; // LightSpecular
                  case 17: tex = rtm->SwapChainBackBuffer; break;
                  case 18: tex = rtm->SwapChainDepthStencil; break;
                }

                if (tex == null || tex->D3D11Texture2D == null) return;

                var srv = GetOrCreateSRV(tex);
                if (srv == null) return;

                if (_dumper == null) {
                    _dumper = new XivMediaPlayer.Utils.TextureDumper();
                    _dumper.Initialize();
                }

                // Get dimensions
                var texPtr = (IntPtr)tex->D3D11Texture2D;
                System.Runtime.InteropServices.Marshal.AddRef(texPtr);
                using var d3dTex = new Vortice.Direct3D11.ID3D11Texture2D(texPtr);
                w = d3dTex.Description.Width;
                h = d3dTex.Description.Height;

                rgbaData = _dumper.DumpTextureToRgba(srv, w, h);
            }

            if (rgbaData != null && w > 0 && h > 0) {
                if (XivMediaPlayer.Utils.ClipboardHelper.CopyBgraToClipboard(w, h, rgbaData)) {
                    _pluginLog.Info($"[Depth Preview] Copied {w}x{h} texture to clipboard.");
                } else {
                    _pluginLog.Warning("[Depth Preview] Failed to set clipboard data.");
                }
            }
        } catch (Exception ex) {
            _pluginLog.Error(ex, "[Depth Preview] Error copying to clipboard.");
        }
    }

      private unsafe void ExportPreviewFrame() {
          if (_sceneRenderer == null || !_sceneRenderer.IsInitialized || _sceneRenderer.PreviewSRV == null) {
              _pluginLog.Error("Preview renderer is not initialized or SRV is null.");
              return;
          }

          var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
          var folderPath = System.IO.Path.Combine(desktopPath, "XivMediaPlayerDumps");
          if (!System.IO.Directory.Exists(folderPath)) {
              System.IO.Directory.CreateDirectory(folderPath);
          }

          var filePath = System.IO.Path.Combine(folderPath, "ReconstructedPreview.png");

          using var dumper = new Utils.TextureDumper();
          if (dumper.Initialize()) {
              // 1. Export the Reconstructed Preview
              var rgbaData = dumper.DumpTextureToRgba(_sceneRenderer.PreviewSRV, 1920, 1080);
              if (rgbaData != null) {
                  using var bmp = new System.Drawing.Bitmap(1920, 1080, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                  var rect = new System.Drawing.Rectangle(0, 0, 1920, 1080);
                  var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                  
                  // TextureDumper already outputs BGRA, so no need to swap B and R!
                  // Just force Alpha to 255 so the PNG isn't transparent.
                  for (int i = 0; i < rgbaData.Length; i += 4) {
                      rgbaData[i + 3] = 255;
                  }
                  
                  System.Runtime.InteropServices.Marshal.Copy(rgbaData, 0, data.Scan0, rgbaData.Length);
                  bmp.UnlockBits(data);
                  bmp.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                  _pluginLog.Information($"Exported Preview to {filePath}");
              }

              // 2. Export the actual Game Frame (SwapChainBackBuffer)
              var rtm = RenderTargetManager.Instance();
              if (rtm != null && rtm->SwapChainBackBuffer != null && rtm->SwapChainBackBuffer->D3D11Texture2D != null) {
                  var sbbPtr = (IntPtr)rtm->SwapChainBackBuffer->D3D11Texture2D;
                  System.Runtime.InteropServices.Marshal.AddRef(sbbPtr);
                  using var d3dTex = new Vortice.Direct3D11.ID3D11Texture2D(sbbPtr);
                  int w = d3dTex.Description.Width;
                  int h = d3dTex.Description.Height;
                  
                  using var device = d3dTex.Device;
                  using var context = device.ImmediateContext;
                  
                  // The SwapChainBackBuffer might not have BindFlags.ShaderResource.
                  // We must copy it to a new texture that does.
                  var desc = d3dTex.Description;
                  desc.BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource;
                  desc.Usage = Vortice.Direct3D11.ResourceUsage.Default;
                  desc.CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None;
                  desc.MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None;
                  
                  using var srvTex = device.CreateTexture2D(desc);
                  
                  if (desc.SampleDescription.Count > 1) {
                      context.ResolveSubresource(srvTex, 0, d3dTex, 0, desc.Format);
                  } else {
                      context.CopyResource(srvTex, d3dTex);
                  }
                  
                  using var srv = device.CreateShaderResourceView(srvTex);
                  
                  var rgbaSbb = dumper.DumpTextureToRgba(srv, w, h);
                  if (rgbaSbb != null) {
                      var sbbPath = System.IO.Path.Combine(folderPath, "GameScreenshot.png");
                      using var bmp2 = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                      var rect2 = new System.Drawing.Rectangle(0, 0, w, h);
                      var data2 = bmp2.LockBits(rect2, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp2.PixelFormat);
                      
                      // TextureDumper already outputs BGRA, so no need to swap B and R!
                      // Force Alpha to 255
                      // (FFXIV stores Alpha=0 for the 3D scene in the backbuffer to use as a UI mask, 
                      // which would make the PNG transparent in image editors if we didn't force it to 255)
                      for (int i = 0; i < rgbaSbb.Length; i += 4) {
                          rgbaSbb[i + 3] = 255;
                      }
                      
                      System.Runtime.InteropServices.Marshal.Copy(rgbaSbb, 0, data2.Scan0, rgbaSbb.Length);
                      bmp2.UnlockBits(data2);
                      bmp2.Save(sbbPath, System.Drawing.Imaging.ImageFormat.Png);
                      _pluginLog.Information($"Exported Game Screenshot to {sbbPath}");
                  }
              }
          }
      }

    private unsafe void DumpRtmFields() {
        try {
            var type = typeof(RenderTargetManager);
            _pluginLog.Info("=== RenderTargetManager Properties ===");
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)) {
                if (prop.PropertyType.Name.Contains("Texture") || prop.PropertyType.Name.Contains("Span")) {
                    _pluginLog.Info($"- {prop.PropertyType.Name} {prop.Name}");
                }
            }

            _pluginLog.Info("=== RenderTargetManager Fields ===");
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)) {
                if (field.FieldType.Name.Contains("Texture") || field.FieldType.Name.Contains("FixedSizeArray")) {
                    var offsetAttr = (System.Runtime.InteropServices.FieldOffsetAttribute)System.Attribute.GetCustomAttribute(field, typeof(System.Runtime.InteropServices.FieldOffsetAttribute));
                    string offsetStr = offsetAttr != null ? $"[FieldOffset(0x{offsetAttr.Value:X})]" : "[NoOffset]";
                    _pluginLog.Info($"{offsetStr} {field.FieldType.Name} {field.Name}");
                }
            }
        } catch (Exception ex) {
            _pluginLog.Error(ex, "Failed to dump RTM fields");
        }
    }

    private unsafe void BulkExportBuffers() {
        try {
            string deskDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "XivMediaPlayerDumps");
            System.IO.Directory.CreateDirectory(deskDir);
            
            var rtm = RenderTargetManager.Instance();
            if (rtm == null) return;
            
            if (_dumper == null) {
                _dumper = new XivMediaPlayer.Utils.TextureDumper();
                _dumper.Initialize();
            }

            void ExportTex(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* tex, string name) {
                if (tex == null || tex->D3D11Texture2D == null) return;
                try {
                    var texPtr = (IntPtr)tex->D3D11Texture2D;
                    System.Runtime.InteropServices.Marshal.AddRef(texPtr);
                    using var d3dTex = new Vortice.Direct3D11.ID3D11Texture2D(texPtr);
                    int w = d3dTex.Description.Width;
                    int h = d3dTex.Description.Height;
                    
                    using var device = d3dTex.Device;
                    using var context = device.ImmediateContext;
                    
                    // The buffer might not have BindFlags.ShaderResource.
                    // Copy to a staging/temp texture that does.
                    var desc = d3dTex.Description;
                    desc.BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource;
                    desc.Usage = Vortice.Direct3D11.ResourceUsage.Default;
                    desc.CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None;
                    desc.MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None;
                    
                    using var srvTex = device.CreateTexture2D(desc);
                    
                    if (desc.SampleDescription.Count > 1) {
                        context.ResolveSubresource(srvTex, 0, d3dTex, 0, desc.Format);
                    } else {
                        context.CopyResource(srvTex, d3dTex);
                    }
                    
                    using var srv = device.CreateShaderResourceView(srvTex);
                    byte[] rgbaData = _dumper.DumpTextureToRgba(srv, w, h);
                    if (rgbaData != null) {
                        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        var rect = new System.Drawing.Rectangle(0, 0, w, h);
                        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                        
                        // TextureDumper already outputs BGRA, so no need to swap B and R!
                        // Force Alpha to 255 to prevent transparent exports
                        for (int i = 0; i < rgbaData.Length; i += 4) {
                            rgbaData[i + 3] = 255;
                        }
                        
                        System.Runtime.InteropServices.Marshal.Copy(rgbaData, 0, data.Scan0, rgbaData.Length);
                        bmp.UnlockBits(data);
                        bmp.Save(System.IO.Path.Combine(deskDir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                } catch(Exception e) {
                    _pluginLog.Error(e, "Error exporting " + name);
                }
            }

            void ExportSpanPtr(Span<FFXIVClientStructs.Interop.Pointer<FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture>> span, string name) {
                for (int i = 0; i < span.Length; i++) {
                    if (span[i].Value != null) {
                        ExportTex(span[i].Value, $"{name}_{i}");
                    }
                }
            }

            try { ExportSpanPtr(rtm->GBuffers, "GBuffers"); } catch {}
            try { ExportSpanPtr(rtm->SemitransparentGBuffers, "SemitransparentGBuffers"); } catch {}
            try { ExportSpanPtr(rtm->Unk160, "Unk160"); } catch {}
            try { ExportSpanPtr(rtm->Unk188, "Unk188"); } catch {}
            try { ExportSpanPtr(rtm->Unk230, "Unk230"); } catch {}
            try { ExportSpanPtr(rtm->Unk2A0, "Unk2A0"); } catch {}
            try { ExportSpanPtr(rtm->Unk2B8, "Unk2B8"); } catch {}
            try { ExportSpanPtr(rtm->CharaViewTextures, "CharaViewTextures"); } catch {}
            try { ExportSpanPtr(rtm->CharaViewGBuffers, "CharaViewGBuffers"); } catch {}
            try { ExportSpanPtr(rtm->CharaViewSemitransparentGBuffers, "CharaViewSemitransparentGBuffers"); } catch {}
            try { ExportSpanPtr(rtm->Unk3E8, "Unk3E8"); } catch {}
            try { ExportSpanPtr(rtm->Unk3F8, "Unk3F8"); } catch {}
            try { ExportSpanPtr(rtm->Unk408, "Unk408"); } catch {}

            ExportTex(*(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + 0x48), "Unk48");
            ExportTex(*(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + 0x50), "Unk50");
            ExportTex(*(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + 0x58), "LightDiffuse");
            ExportTex(*(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + 0x60), "LightSpecular");
            ExportTex(*(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + 0x68), "Unk68");
            ExportTex(*(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + 0x70), "Unk70");
            ExportTex(*(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + 0x78), "Unk78");
            ExportTex(*(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + 0x80), "Unk80");
            ExportTex(rtm->ToneAdjustSource, "ToneAdjustSource");

            _pluginLog.Info("Bulk export completed to Desktop!");
        } catch(Exception ex) {
            _pluginLog.Error(ex, "Error bulk exporting.");
        }
    }
  }
}
