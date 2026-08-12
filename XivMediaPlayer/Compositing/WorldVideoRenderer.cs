using Dalamud.Interface.Textures.TextureWraps;
using MediaPlayerCore.Compositing;
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using System.Runtime.CompilerServices;

namespace XivMediaPlayer.Compositing {
  internal enum WorldTvRenderPass {
    Full,
    GlowOnly,
    CompositeOnly
  }

  /// <summary>
  /// Renders the VLC video frame as a textured quad in 3D world space.
  /// Supports two render modes:
  ///  1. ImGui mode (default): projects quad corners to screen space via WorldToScreen,
  ///   draws with ImGui.AddImageQuad. No occlusion.
  ///  2. D3D11 depth-tested mode: renders as native D3D11 geometry using the game's
  ///   depth buffer for per-pixel occlusion by walls, characters, etc.
  /// </summary>
  internal class WorldVideoRenderer : IDisposable {
    private readonly WorldScreenTransform _transform;
    private readonly IGameGui? _gameGui;
    private DepthTestedRenderer? _depthRenderer;
    private GlowRenderer? _glowRenderer;
    private VignetteExtractor? _vignetteExtractor;
    private bool _disposed;
    private bool _useDepthOcclusion = true;
    private bool _enableGlow = true;
    private bool _enableUiCulling = true;
    
    private unsafe FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* _lastGBuffer2Tex;
    private unsafe FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* _lastGBuffer3Tex;
    private Vortice.Direct3D11.ID3D11ShaderResourceView? _gbuffer2Srv;
    private Vortice.Direct3D11.ID3D11ShaderResourceView? _gbuffer3Srv;
    
    private unsafe FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* _lastUnk68Tex;
    private Vortice.Direct3D11.ID3D11Texture2D? _unk68CopyTex;
    private Vortice.Direct3D11.ID3D11ShaderResourceView? _unk68Srv;

    /// <summary>
    /// Whether to render a backlit glow effect around the screen.
    /// </summary>
    public bool EnableGlow { get => _enableGlow; set => _enableGlow = value; }

    public bool EnableUiCulling { get => _enableUiCulling; set => _enableUiCulling = value; }

    /// <summary>Clears shared corner jitter state before rendering another TV in the same frame.</summary>
    public void ResetCornerStabilization() {
      _lastCorners = null;
    }

    /// <summary>Prepare the depth renderer to retain separate composite textures for each TV this frame.</summary>
    public void BeginMultiTvCompositeFrame() {
      if (_depthRenderer == null) {
        _depthRenderer = new DepthTestedRenderer();
      }

      if (!_depthRenderer.IsInitialized) {
        _depthRenderer.Initialize();
      }

      _depthRenderer.BeginMultiOutputFrame();
    }

    public WorldScreenTransform Transform => _transform;

    /// <summary>
    /// Whether world rendering is currently active.
    /// </summary>
    public bool IsActive {
      get => _transform.Enabled;
      set => _transform.Enabled = value;
    }

    /// <summary>
    /// When true, renders via D3D11 with depth testing for occlusion.
    /// When false, renders via ImGui overlay (no occlusion).
    /// </summary>
    public bool UseDepthOcclusion {
      get => _useDepthOcclusion;
      set {
        _useDepthOcclusion = value;
        if (value && _depthRenderer == null) {
          _depthRenderer = new DepthTestedRenderer();
        }
      }
    }

    /// <summary>
    /// Returns initialization error from the depth renderer, if any.
    /// </summary>
    public string? DepthRendererError { get; private set; }

    public WorldVideoRenderer(WorldScreenTransform transform, IGameGui? gameGui = null) {
      _transform = transform ?? new WorldScreenTransform();
      _gameGui = gameGui;
    }

    /// <summary>
    /// Renders the video texture as a 3D quad in world space.
    /// </summary>
    public void Render(
        IntPtr textureSrv, int textureWidth, int textureHeight, int textureTrueWidth, int textureTrueHeight, DepthBufferCapture depthCapture = null,
      Vector3? cameraPos = null, Vector3? cameraForward = null, Vector3? cameraRight = null, Vector3? cameraUp = null,
      float fovY = MathF.PI / 4, float aspectRatio = 1.0f, UILayerCapture uiCapture = null, float nearPlane = 0.1f, float farPlane = 10000f,
      Vector2? hoverUV = null, float progress = 0f, float bufferProgress = 1f, float playbackState = 0f, float lockState = 1.0f, float volume = 1.0f, IntPtr titleSrvPtr = default, bool isLooping = false, bool isShuffle = false, float time = 0f, float showScreensaver = 0f, bool useDifferenceFallback = false,
      Matrix4x4? viewProjMatrix = null, Vector2? viewportPos = null, Vector2? viewportSize = null, float uiBlendThreshold = 0.0f, float loadingPulse = 0f, bool isLoadingOverlay = false, IntPtr idleBrandingSrvPtr = default, float idleBrandingAspect = 1.0f,
      WorldScreenTransform? screenTransform = null,
      bool isolateCompositeOutput = false,
      WorldTvRenderPass renderPass = WorldTvRenderPass.Full,
      bool preserveSortedDrawOrder = false) {

      var placement = screenTransform ?? _transform;
      if (_disposed || !placement.Enabled || textureSrv == IntPtr.Zero) return;
      
      var drawList = Dalamud.Bindings.ImGui.ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());
      int initialCmdSize = drawList.CmdBuffer.Size;
      bool pushCompositeToFront = renderPass != WorldTvRenderPass.GlowOnly && !preserveSortedDrawOrder;

      float trueVidHeight = textureTrueHeight > 0 ? textureTrueHeight : 0;
      float trueVidWidth = textureTrueWidth > 0 ? textureTrueWidth : 0;
      // Never derive aspect from padded texture dimensions. VLC aligns height to 32px
      // (e.g. 1080p in a 1088-tall buffer), which falsely triggers letterboxing.
      float videoAspect = (trueVidWidth > 0 && trueVidHeight > 0) ? trueVidWidth / trueVidHeight : 0f;
      float uvBottom = (textureTrueHeight > 0 && textureHeight > 0) ? ((float)textureTrueHeight / textureHeight) : 1.0f;
      float uvRight = (textureTrueWidth > 0 && textureWidth > 0) ? ((float)textureTrueWidth / textureWidth) : 1.0f;

      if (cameraPos.HasValue && cameraForward.HasValue) {
        var (tl, tr, br, bl) = placement.Corners;
        float zTL = Vector3.Dot(tl - cameraPos.Value, -cameraForward.Value);
        float zTR = Vector3.Dot(tr - cameraPos.Value, -cameraForward.Value);
        float zBR = Vector3.Dot(br - cameraPos.Value, -cameraForward.Value);
        float zBL = Vector3.Dot(bl - cameraPos.Value, -cameraForward.Value);
        
        bool bypassCulling = _useDepthOcclusion && depthCapture != null;

        if (!bypassCulling) {
          // Prevent rendering when all corners are behind camera plane to avoid perspective wrap-around.
          if (zTL <= 0.1f && zTR <= 0.1f && zBR <= 0.1f && zBL <= 0.1f) {
              return;
          }

          // Prevent rendering when any corner is far behind the camera to avoid polygon stretching.
          if (zTL <= -2f || zTR <= -2f || zBR <= -2f || zBL <= -2f) {
              return;
          }
        }
      }

      if (_useDepthOcclusion && depthCapture != null && cameraPos.HasValue && cameraForward.HasValue && cameraRight.HasValue && cameraUp.HasValue) {
        var (tl, tr, br, bl) = placement.Corners;
        float zTL = cameraPos.HasValue && cameraForward.HasValue ? Vector3.Dot(tl - cameraPos.Value, -cameraForward.Value) : 1f;
        float zTR = cameraPos.HasValue && cameraForward.HasValue ? Vector3.Dot(tr - cameraPos.Value, -cameraForward.Value) : 1f;
        float zBR = cameraPos.HasValue && cameraForward.HasValue ? Vector3.Dot(br - cameraPos.Value, -cameraForward.Value) : 1f;
        float zBL = cameraPos.HasValue && cameraForward.HasValue ? Vector3.Dot(bl - cameraPos.Value, -cameraForward.Value) : 1f;
        bool allCornersInFront = zTL > 0.1f && zTR > 0.1f && zBR > 0.1f && zBL > 0.1f;

        RenderWithOcclusion(placement, textureSrv, depthCapture, cameraPos.Value,
          cameraForward.Value, cameraRight.Value, cameraUp.Value, fovY, aspectRatio, uiCapture, nearPlane, farPlane, hoverUV, progress, bufferProgress, playbackState, lockState, volume, titleSrvPtr, isLooping, isShuffle, time, showScreensaver, videoAspect, allCornersInFront, useDifferenceFallback, viewProjMatrix, viewportPos, viewportSize, uiBlendThreshold, uvBottom, uvRight, loadingPulse, isLoadingOverlay, idleBrandingSrvPtr, idleBrandingAspect, renderPass, isolateCompositeOutput);
      } else if (renderPass != WorldTvRenderPass.CompositeOnly) {
        RenderScreenSpace(placement, textureSrv, videoAspect, viewProjMatrix, viewportPos, viewportSize, uvBottom, uvRight, renderPass);
      }
      
      if (pushCompositeToFront) {
        PushCommandsToFront(drawList, initialCmdSize);
      }
    }

    private unsafe void PushCommandsToFront(Dalamud.Bindings.ImGui.ImDrawListPtr drawList, int startIndex) {
        var cmdBuffer = drawList.CmdBuffer;
        int count = cmdBuffer.Size;
        if (startIndex <= 0 || startIndex >= count) return;

        int addedCount = count - startIndex;
        Dalamud.Bindings.ImGui.ImDrawCmd* ptr = (Dalamud.Bindings.ImGui.ImDrawCmd*)cmdBuffer.Data;
        
        // Backup the newly added commands
        var newCmds = new Dalamud.Bindings.ImGui.ImDrawCmd[addedCount];
        for (int i = 0; i < addedCount; i++) {
            newCmds[i] = ptr[startIndex + i];
        }

        // Shift existing commands up
        for (int i = startIndex - 1; i >= 0; i--) {
            ptr[i + addedCount] = ptr[i];
        }

        // Copy new commands to the front
        for (int i = 0; i < addedCount; i++) {
            ptr[i] = newCmds[i];
        }
    }

    /// <summary>
    /// Debug info: per-corner depth thresholds and sampled game depths at corners.
    /// </summary>
    public string DepthDebugInfo { get; private set; }

    private (Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl)? _lastCorners;

    private void StabilizeCorners(ref Vector2 tl, ref Vector2 tr, ref Vector2 br, ref Vector2 bl) {
      if (_lastCorners.HasValue) {
        var last = _lastCorners.Value;
        float diff = Vector2.Distance(tl, last.tl) + Vector2.Distance(tr, last.tr) + Vector2.Distance(br, last.br) + Vector2.Distance(bl, last.bl);
        if (diff < 4.0f) {
          tl = last.tl;
          tr = last.tr;
          br = last.br;
          bl = last.bl;
        } else {
          _lastCorners = (tl, tr, br, bl);
        }
      } else {
        _lastCorners = (tl, tr, br, bl);
      }
    }

    private unsafe Vortice.Direct3D11.ID3D11ShaderResourceView GetOrCreateSRV(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* tex, ref FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* lastTex, ref Vortice.Direct3D11.ID3D11ShaderResourceView srv) {
        if (tex == null || tex->D3D11Texture2D == null) return null;
        if (tex != lastTex || srv == null) {
            srv?.Dispose();
            srv = null;
            lastTex = tex;
            
            var texPtr = (IntPtr)tex->D3D11Texture2D;
            System.Runtime.InteropServices.Marshal.AddRef(texPtr);
            var d3dTex = new Vortice.Direct3D11.ID3D11Texture2D(texPtr);
            var device = d3dTex.Device;
            
            try {
                if (d3dTex.Description.Format == Vortice.DXGI.Format.R24G8_Typeless) {
                    var desc = new Vortice.Direct3D11.ShaderResourceViewDescription {
                        Format = Vortice.DXGI.Format.R24_UNorm_X8_Typeless,
                        ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                        Texture2D = new Vortice.Direct3D11.Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
                    };
                    srv = device.CreateShaderResourceView(d3dTex, desc);
                } else if (d3dTex.Description.Format == Vortice.DXGI.Format.R32_Typeless) {
                    var desc = new Vortice.Direct3D11.ShaderResourceViewDescription {
                        Format = Vortice.DXGI.Format.R32_Float,
                        ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                        Texture2D = new Vortice.Direct3D11.Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
                    };
                    srv = device.CreateShaderResourceView(d3dTex, desc);
                } else if (d3dTex.Description.Format.ToString().Contains("Typeless")) {
                    Vortice.DXGI.Format newFmt = d3dTex.Description.Format;
                    if (newFmt == Vortice.DXGI.Format.R8G8B8A8_Typeless) newFmt = Vortice.DXGI.Format.R8G8B8A8_UNorm;
                    else if (newFmt == Vortice.DXGI.Format.R16G16B16A16_Typeless) newFmt = Vortice.DXGI.Format.R16G16B16A16_Float;
                    else if (newFmt == Vortice.DXGI.Format.R32G32B32A32_Typeless) newFmt = Vortice.DXGI.Format.R32G32B32A32_Float;
                    else if (newFmt == Vortice.DXGI.Format.R10G10B10A2_Typeless) newFmt = Vortice.DXGI.Format.R10G10B10A2_UNorm;
                    
                    var desc = new Vortice.Direct3D11.ShaderResourceViewDescription {
                        Format = newFmt,
                        ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                        Texture2D = new Vortice.Direct3D11.Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
                    };
                    srv = device.CreateShaderResourceView(d3dTex, desc);
                } else {
                    srv = device.CreateShaderResourceView(d3dTex);
                }
            } catch (Exception ex) {
                // Ignore
            } finally {
                device?.Dispose();
                d3dTex?.Dispose();
            }
        }
        return srv;
    }

    private unsafe Vortice.Direct3D11.ID3D11ShaderResourceView GetCopiedSRV(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* tex, ref FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* lastTex, ref Vortice.Direct3D11.ID3D11Texture2D copyTex, ref Vortice.Direct3D11.ID3D11ShaderResourceView srv) {
        if (tex == null || tex->D3D11Texture2D == null) return null;
        
        var texPtr = (IntPtr)tex->D3D11Texture2D;
        var d3dTex = new Vortice.Direct3D11.ID3D11Texture2D(texPtr);
        var device = d3dTex.Device;
        
        try {
            var desc = d3dTex.Description;
            if (tex != lastTex || copyTex == null || copyTex.Description.Width != desc.Width || copyTex.Description.Height != desc.Height || copyTex.Description.Format != desc.Format) {
                srv?.Dispose();
                srv = null;
                copyTex?.Dispose();
                copyTex = null;
                lastTex = tex;
                
                var copyDesc = desc;
                copyDesc.BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource;
                copyDesc.Usage = Vortice.Direct3D11.ResourceUsage.Default;
                copyDesc.CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None;
                copyDesc.MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None;
                
                // Ensure format is SRV compatible
                if (copyDesc.Format == Vortice.DXGI.Format.R24G8_Typeless) copyDesc.Format = Vortice.DXGI.Format.R24_UNorm_X8_Typeless;
                else if (copyDesc.Format == Vortice.DXGI.Format.R32_Typeless) copyDesc.Format = Vortice.DXGI.Format.R32_Float;
                else if (copyDesc.Format.ToString().Contains("Typeless")) {
                    if (copyDesc.Format == Vortice.DXGI.Format.R8G8B8A8_Typeless) copyDesc.Format = Vortice.DXGI.Format.R8G8B8A8_UNorm;
                    else if (copyDesc.Format == Vortice.DXGI.Format.R16G16B16A16_Typeless) copyDesc.Format = Vortice.DXGI.Format.R16G16B16A16_Float;
                    else if (copyDesc.Format == Vortice.DXGI.Format.R32G32B32A32_Typeless) copyDesc.Format = Vortice.DXGI.Format.R32G32B32A32_Float;
                    else if (copyDesc.Format == Vortice.DXGI.Format.R10G10B10A2_Typeless) copyDesc.Format = Vortice.DXGI.Format.R10G10B10A2_UNorm;
                }
                
                copyTex = device.CreateTexture2D(copyDesc);
                srv = device.CreateShaderResourceView(copyTex);
            }
            
            var context = device.ImmediateContext;
            if (desc.SampleDescription.Count > 1) {
                context.ResolveSubresource(copyTex, 0, d3dTex, 0, desc.Format);
            } else {
                context.CopyResource(copyTex, d3dTex);
            }
            context.Dispose();
        } catch (Exception ex) {
            // Ignore
        } finally {
            device?.Dispose();
            d3dTex?.Dispose();
        }
        
        return srv;
    }

    /// <summary>
    /// GPU-accelerated per-pixel depth occlusion. Uses WorldToScreen for positioning
    /// and view-space Z (dot with camera forward) for depth thresholds.
    /// </summary>
    private unsafe void RenderWithOcclusion(WorldScreenTransform placement, IntPtr textureSrv, DepthBufferCapture depthCapture,
      Vector3 cameraPos, Vector3 cameraForward, Vector3 cameraRight, Vector3 cameraUp, float fovY, float aspectRatio, UILayerCapture uiCapture,
      float nearPlane, float farPlane, Vector2? hoverUV, float progress, float bufferProgress, float playbackState, float lockState, float volume, IntPtr titleSrvPtr, bool isLooping, bool isShuffle, float time, float showScreensaver, float videoAspectRatio, bool allCornersInFront, bool useDifferenceFallback,
      Matrix4x4? viewProjMatrix, Vector2? viewportPos, Vector2? viewportSize, float uiBlendThreshold, float uvBottom, float uvRight, float loadingPulse = 0f, bool isLoadingOverlay = false, IntPtr idleBrandingSrvPtr = default, float idleBrandingAspect = 1.0f,
      WorldTvRenderPass renderPass = WorldTvRenderPass.Full, bool isolateCompositeOutput = false) {
      bool renderGlow = renderPass != WorldTvRenderPass.CompositeOnly;
      bool renderComposite = renderPass != WorldTvRenderPass.GlowOnly;
      var (tl, tr, br, bl) = placement.Corners;
      
      var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
      if (rtm != null) {
          GetOrCreateSRV(rtm->GBuffers[2].Value, ref _lastGBuffer2Tex, ref _gbuffer2Srv);
          GetOrCreateSRV(rtm->GBuffers[3].Value, ref _lastGBuffer3Tex, ref _gbuffer3Srv);
          
          var unk68 = *(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + 0x68);
          GetCopiedSRV(unk68, ref _lastUnk68Tex, ref _unk68CopyTex, ref _unk68Srv);
      }
      
      if (_vignetteExtractor != null && uiCapture?.BackBufferSRV != null && _unk68Srv != null && depthCapture?.CapturedSRV != null) {
          _vignetteExtractor.Update(uiCapture.BackBufferSRV, _unk68Srv, depthCapture.CapturedSRV);
      }

      // WorldToScreen is the source of truth for screen positions
      WorldToScreenClamped(tl, out var sTL, out _, viewProjMatrix, viewportPos, viewportSize);
      WorldToScreenClamped(tr, out var sTR, out _, viewProjMatrix, viewportPos, viewportSize);
      WorldToScreenClamped(br, out var sBR, out _, viewProjMatrix, viewportPos, viewportSize);
      WorldToScreenClamped(bl, out var sBL, out _, viewProjMatrix, viewportPos, viewportSize);

      StabilizeCorners(ref sTL, ref sTR, ref sBR, ref sBL);

      // Compute per-corner depth using view-space Z (distance along camera forward)
      // This matches what the depth buffer stores, unlike Euclidean distance
      // Note: FFXIV view matrix Z-axis points AWAY from look direction, so negate
      float ComputeDepth(Vector3 corner) {
        float viewZ = Vector3.Dot(corner - cameraPos, -cameraForward);
        if (viewZ <= 0) return 0f;
        float d = nearPlane * (farPlane - viewZ) / (viewZ * (farPlane - nearPlane));
        return Math.Clamp(d, 0f, 1f);
      }
      float depthTL = ComputeDepth(tl);
      float depthTR = ComputeDepth(tr);
      float depthBR = ComputeDepth(br);
      float depthBL = ComputeDepth(bl);
      var cornerDepths = new Vector4(depthTL, depthTR, depthBR, depthBL);

      // Sample game depth at corners for debug
      float gameTL = depthCapture.GetDepthAt((int)sTL.X, (int)sTL.Y);
      float gameTR = depthCapture.GetDepthAt((int)sTR.X, (int)sTR.Y);
      float gameBR = depthCapture.GetDepthAt((int)sBR.X, (int)sBR.Y);
      float gameBL = depthCapture.GetDepthAt((int)sBL.X, (int)sBL.Y);
      DepthDebugInfo = $"Threshold: TL={depthTL:F6} TR={depthTR:F6} BR={depthBR:F6} BL={depthBL:F6}\n" +
                        $"GameDepth: TL={gameTL:F6} TR={gameTR:F6} BR={gameBR:F6} BL={gameBL:F6}\n" +
                        $"Occluded?: TL={gameTL > depthTL} TR={gameTR > depthTR} BR={gameBR > depthBR} BL={gameBL > depthBL}";

      // Feed screen quad info to depth capture for preview overlay
      depthCapture.ScreenQuadCorners = (sTL, sTR, sBR, sBL);
      depthCapture.ScreenQuadDepths = cornerDepths;

      float centerDepth = (depthTL + depthTR + depthBR + depthBL) * 0.25f;

      var drawList = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());

      // Depth-tested halo behind the TV (drawn before the composited overlay).
      if (renderGlow && _enableGlow && allCornersInFront) {
        float visibility = ComputeVisibility(depthCapture, sTL, sTR, sBR, sBL, centerDepth);
        if (visibility > 0.05f) {
          RenderGlow(drawList, textureSrv, sTL, sTR, sBR, sBL, visibility);
        }
      }

      if (!renderComposite) {
        return;
      }

        // Render standard UI layer (non-occluded TV UI)
        // Ensure DepthTestedRenderer is initialized
        if (_depthRenderer == null) {
          _depthRenderer = new DepthTestedRenderer();
          _depthRenderer.Initialize();
          _vignetteExtractor = new VignetteExtractor();
          _vignetteExtractor.Initialize();
        }
        if (!_depthRenderer.IsInitialized) {
          if (!_depthRenderer.Initialize()) {
            DepthRendererError = $"Init failed: {_depthRenderer.InitError}";
            RenderScreenSpace(placement, textureSrv, videoAspectRatio, viewProjMatrix, viewportPos, viewportSize, uvBottom, uvRight);
            return;
          }
        }

      // Get viewport size
      var viewport = ImGui.GetMainViewport();
      int screenW = (int)viewport.Size.X;
      int screenH = (int)viewport.Size.Y;

      // Get the video texture SRV pointer
      var videoSrvPtr = textureSrv;

      try {
        if (depthCapture.CapturedSRV == null) {
          DepthRendererError = "Depth SRV not available";
          RenderScreenSpace(placement, textureSrv, videoAspectRatio, viewProjMatrix, viewportPos, viewportSize, uvBottom, uvRight);
          return;
        }

        // ImGui/Dalamud coordinates are in desktop-space (viewport.Pos might not be 0,0).
        // DepthTestedRenderer expects local coordinates (0 to screenW, 0 to screenH).
        var localTL = sTL - viewport.Pos;
        var localTR = sTR - viewport.Pos;
        var localBR = sBR - viewport.Pos;
        var localBL = sBL - viewport.Pos;

        depthCapture.GetMinMaxDepth(out float minDepth, out float maxDepth);

        var transparentUiSrvPtr = SceneColorProbe.GetToneAdjustSourceSrvPtr();

        // Per-corner depths interpolated in shader for correct angled-view occlusion
        bool success = _depthRenderer.Render(
          (localTL, localTR, localBR, localBL),
          (tl, tr, br, bl),
          cameraPos,
          cameraForward, cameraRight, cameraUp, fovY, aspectRatio,
          videoSrvPtr,
          depthCapture.CapturedSRV,
          cornerDepths,
          nearPlane, farPlane,
          screenW, screenH,
          _enableUiCulling ? uiCapture?.BackBufferSRV : null,
          hoverUV, progress, bufferProgress, playbackState, lockState,
          minDepth, maxDepth, volume,
          depthCapture.RenderWidth, depthCapture.RenderHeight,
          _enableUiCulling ? uiCapture?.LastAddonRects : null, titleSrvPtr, isLooping, isShuffle, time, showScreensaver, videoAspectRatio,
          _gbuffer2Srv?.NativePointer ?? IntPtr.Zero,
          _gbuffer3Srv?.NativePointer ?? IntPtr.Zero,
          _unk68Srv?.NativePointer ?? IntPtr.Zero,
          _vignetteExtractor?.ExtrapolatedVignetteSRV?.NativePointer ?? IntPtr.Zero,
          useDifferenceFallback,
          placement.Opacity,
          placement.IsProjectorMode,
          placement.ScreensaverColor,
          placement.ScreensaverStyle,
          uiBlendThreshold, uvBottom, uvRight, _enableGlow && renderGlow, loadingPulse, isLoadingOverlay, idleBrandingSrvPtr, idleBrandingAspect,
          surfaceOnly: isolateCompositeOutput);

        DepthDebugInfo = $"Cam: {cameraPos:F1}\nFwd: {cameraForward:F2}\nFov: {fovY:F3}\nAspect: {aspectRatio:F3}";

        if (success && _depthRenderer.OutputSRV != null) {
          var snapshotSrv = isolateCompositeOutput ? _depthRenderer.AcquireOutputSnapshot() : _depthRenderer.OutputSRV;
          if (snapshotSrv == null) {
            RenderScreenSpace(placement, textureSrv, videoAspectRatio, viewProjMatrix, viewportPos, viewportSize, uvBottom, uvRight);
            return;
          }

          var outputPtr = snapshotSrv.NativePointer;
          var outputId = Unsafe.As<IntPtr, ImTextureID>(ref outputPtr);

          if (isolateCompositeOutput) {
            float invW = screenW > 0 ? 1f / screenW : 1f;
            float invH = screenH > 0 ? 1f / screenH : 1f;
            var uvTL = new Vector2(localTL.X * invW, localTL.Y * invH);
            var uvTR = new Vector2(localTR.X * invW, localTR.Y * invH);
            var uvBR = new Vector2(localBR.X * invW, localBR.Y * invH);
            var uvBL = new Vector2(localBL.X * invW, localBL.Y * invH);
            drawList.AddImageQuad(
              outputId,
              sTL, sTR, sBR, sBL,
              uvTL, uvTR, uvBR, uvBL,
              0xFFFFFFFF);
          } else {
            drawList.AddImage(
              outputId,
              viewport.Pos,
              viewport.Pos + viewport.Size);
          }
          DepthRendererError = null;
        } else {
          if (uiCapture == null || !uiCapture.IsInitialized) {
            RenderScreenSpace(placement, textureSrv, videoAspectRatio, viewProjMatrix, viewportPos, viewportSize, uvBottom, uvRight);
          }
        }
      } catch (Exception ex) {
        // Fallback to screen space if custom shader fails
        RenderScreenSpace(placement, textureSrv, videoAspectRatio, viewProjMatrix, viewportPos, viewportSize, uvBottom, uvRight);
      }
    }

    private static Vector2 Bilerp(Vector2 tl, Vector2 tr, Vector2 bl, Vector2 br, float u, float v) {
      var top = Vector2.Lerp(tl, tr, u);
      var bot = Vector2.Lerp(bl, br, u);
      return Vector2.Lerp(top, bot, v);
    }

    private static uint DepthToColor(DepthBufferCapture depthCapture, Vector2 screenPos, float threshold) {
      float depth = depthCapture.GetDepthAt((int)screenPos.X, (int)screenPos.Y);
      // In reverse-Z: higher depth = closer. If game geometry is closer (depth > threshold),
      // the quad should be occluded (alpha = 0).
      byte alpha = depth > threshold ? (byte)0 : (byte)255;
      return (uint)(alpha << 24) | 0x00FFFFFF; // ABGR format
    }

    /// <summary>
    /// Multi-sample depth within a cell area for anti-aliased occlusion edges.
    /// Returns an alpha-modulated white color.
    /// </summary>
    private static uint DepthToColorAA(DepthBufferCapture depthCapture,
      Vector2 sTL, Vector2 sTR, Vector2 sBL, Vector2 sBR,
      float u0, float v0, float u1, float v1, float threshold) {
      const int subSamples = 3; // 3x3 = 9 samples per cell
      int passing = 0;
      int total = subSamples * subSamples;

      for (int sy = 0; sy < subSamples; sy++) {
        for (int sx = 0; sx < subSamples; sx++) {
          float su = u0 + (u1 - u0) * (sx + 0.5f) / subSamples;
          float sv = v0 + (v1 - v0) * (sy + 0.5f) / subSamples;
          var samplePos = Bilerp(sTL, sTR, sBL, sBR, su, sv);
          float depth = depthCapture.GetDepthAt((int)samplePos.X, (int)samplePos.Y);
          if (depth <= threshold) passing++;
        }
      }

      byte alpha = (byte)(passing * 255 / total);
      return (uint)(alpha << 24) | 0x00FFFFFF;
    }

    /// <summary>
    /// Renders using ImGui screen-space projection (no occlusion).
    /// </summary>
    private void RenderScreenSpace(WorldScreenTransform placement, IntPtr textureSrv, float videoAspect, Matrix4x4? viewProjMatrix, Vector2? viewportPos, Vector2? viewportSize, float uvBottom = 1.0f, float uvRight = 1.0f, WorldTvRenderPass renderPass = WorldTvRenderPass.Full) {
      bool renderGlow = renderPass != WorldTvRenderPass.CompositeOnly;
      bool renderComposite = renderPass != WorldTvRenderPass.GlowOnly;
      var (tl, tr, br, bl) = placement.Corners;

      WorldToScreenClamped(tl, out var sTL, out _, viewProjMatrix, viewportPos, viewportSize);
      WorldToScreenClamped(tr, out var sTR, out _, viewProjMatrix, viewportPos, viewportSize);
      WorldToScreenClamped(br, out var sBR, out _, viewProjMatrix, viewportPos, viewportSize);
      WorldToScreenClamped(bl, out var sBL, out _, viewProjMatrix, viewportPos, viewportSize);

      StabilizeCorners(ref sTL, ref sTR, ref sBR, ref sBL);

      var drawList = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());

      // Draw backlit glow layers behind the video
      if (renderGlow && _enableGlow) {
        RenderGlow(drawList, textureSrv, sTL, sTR, sBR, sBL, 1f); // no occlusion = full glow
      }

      if (!renderComposite) {
        return;
      }

      // Build the TV texture coordinates based on aspect ratio
      var texId = textureSrv;
      var uvTL = new Vector2(0, 0);
        var uvTR = new Vector2(uvRight, 0);
        var uvBR = new Vector2(uvRight, uvBottom);
        var uvBL = new Vector2(0, uvBottom);

      if (videoAspect > 0) {
        float quadW = Vector3.Distance(tl, tr);
        float quadH = Vector3.Distance(tl, bl);
        float quadAspect = quadH > 0 ? quadW / quadH : 1.0f;
        float aspectDiff = MathF.Abs(quadAspect - videoAspect) / MathF.Max(quadAspect, videoAspect);
        
          if (aspectDiff > 0.005f && videoAspect > quadAspect) {
              float scale = quadAspect / videoAspect;
              float offset = (1.0f - scale) * 0.5f;
              uvTL.X = offset * uvRight;
              uvBL.X = offset * uvRight;
              uvTR.X = uvRight * (1.0f - offset);
              uvBR.X = uvRight * (1.0f - offset);
            } else if (aspectDiff > 0.005f && videoAspect < quadAspect) {
              float scale = videoAspect / quadAspect;
              float offset = (1.0f - scale) * 0.5f;
              Vector2 origTL = sTL;
              Vector2 origTR = sTR;
              Vector2 origBL = sBL;
              Vector2 origBR = sBR;
              sTL = Vector2.Lerp(origTL, origTR, offset);
              sBL = Vector2.Lerp(origBL, origBR, offset);
              sTR = Vector2.Lerp(origTR, origTL, offset);
              sBR = Vector2.Lerp(origBR, origBL, offset);
            }
      }

      byte alpha = (byte)(Math.Clamp(placement.Opacity, 0f, 1f) * 255f);
      uint color = (uint)(alpha << 24) | 0x00FFFFFF;

      var currentId = System.Runtime.CompilerServices.Unsafe.As<IntPtr, Dalamud.Bindings.ImGui.ImTextureID>(ref textureSrv);
      drawList.AddImageQuad(
        currentId,
        sTL, sTR, sBR, sBL,
        uvTL, uvTR, uvBR, uvBL,
        color);
    }

    /// <summary>
    /// Draws soft glow layers behind the video quad to simulate screen illumination.
    /// Uses the video texture itself so the glow color naturally matches the content.
    /// </summary>
    private void RenderGlow(ImDrawListPtr drawList, IntPtr textureSrv,
      Vector2 sTL, Vector2 sTR, Vector2 sBR, Vector2 sBL, float visibility) {
      if (visibility <= 0.01f) return; // fully occluded, no glow

      // Lazy-init the GPU glow renderer
      if (_glowRenderer == null) {
        _glowRenderer = new GlowRenderer();
        _glowRenderer.Initialize(64); // 64x64 downsample with shader vignette
      }
      if (!_glowRenderer.IsInitialized) return;

      // Update the glow texture (GPU downsample + vignette in one shader pass)
      if (!_glowRenderer.UpdateFromVideoTexture(textureSrv)) return;
      var glowPtr = _glowRenderer.GlowTextureHandle;
      if (glowPtr == IntPtr.Zero) return;
      var glowId = Unsafe.As<IntPtr, ImTextureID>(ref glowPtr);

      // Single draw call. Vignette is baked into the texture so edges fade naturally.
      var center = (sTL + sTR + sBR + sBL) * 0.25f;
      float scale = 1.45f;
      var gTL = center + (sTL - center) * scale;
      var gTR = center + (sTR - center) * scale;
      var gBR = center + (sBR - center) * scale;
      var gBL = center + (sBL - center) * scale;

      // Flat 90% occlusion on glow layer. Always renders at 10% strength.
      byte alpha = (byte)(144 * 0.10f);
      uint color = (uint)(alpha << 24) | 0x00FFFFFF;

      drawList.AddImageQuad(
        glowId,
        gTL, gTR, gBR, gBL,
        new Vector2(0, 0), new Vector2(1, 0),
        new Vector2(1, 1), new Vector2(0, 1),
        color);
    }

    /// <summary>
    /// Samples a sparse grid across the screen quad area to determine
    /// what fraction of the screen is visible (not occluded by depth).
    /// </summary>
    private float ComputeVisibility(DepthBufferCapture depthCapture,
      Vector2 sTL, Vector2 sTR, Vector2 sBR, Vector2 sBL, float threshold) {
      const int sampleGrid = 8; // 8x8 = 64 samples (cheap)
      int passing = 0;
      int total = 0;

      for (int sy = 0; sy < sampleGrid; sy++) {
        for (int sx = 0; sx < sampleGrid; sx++) {
          float u = (sx + 0.5f) / sampleGrid;
          float v = (sy + 0.5f) / sampleGrid;
          var pos = Bilerp(sTL, sTR, sBL, sBR, u, v);

          float depth = depthCapture.GetDepthAt((int)pos.X, (int)pos.Y);
          if (depth <= threshold) passing++; // not occluded
          total++;
        }
      }

      return total > 0 ? passing / (float)total : 1f;
    }

    /// <summary>
    /// Computes visibility for glow using the CPU depth readback.
    /// Derives the threshold from camera distance to the quad center.
    /// </summary>
    private float ComputeVisibilityFromGPU(WorldScreenTransform placement, DepthBufferCapture depthCapture,
      Vector2 sTL, Vector2 sTR, Vector2 sBR, Vector2 sBL,
      Vector3 cameraPos, float nearPlane, float farPlane) {
      var (tl, tr, br, bl) = placement.Corners;
      var quadCenter = (tl + tr + br + bl) * 0.25f;
      float distance = Vector3.Distance(cameraPos, quadCenter);

      float quadDepth = nearPlane * (farPlane - distance) / (distance * (farPlane - nearPlane));
      quadDepth = Math.Clamp(quadDepth, 0f, 1f);

      return ComputeVisibility(depthCapture, sTL, sTR, sBL, sBR, quadDepth);
    }

    private bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos) {
      if (_gameGui != null) {
        return _gameGui.WorldToScreen(worldPos, out screenPos);
      }
      screenPos = Vector2.Zero;
      return false;
    }

    /// <summary>
    /// Projects a world position to screen with coordinate clamping to prevent
    /// extreme values from causing ImGui rendering artifacts.
    /// </summary>
    private bool WorldToScreenClamped(Vector3 worldPos, out Vector2 screenPos, out bool isBehind, Matrix4x4? viewProjMatrix, Vector2? viewportPos, Vector2? viewportSize) {
      screenPos = Vector2.Zero;
      isBehind = false;

      bool onScreen = false;
      if (viewProjMatrix.HasValue && viewportPos.HasValue && viewportSize.HasValue) {
        var p = Vector4.Transform(new Vector4(worldPos, 1.0f), viewProjMatrix.Value);
        if (p.W >= 0.1f) {
            p /= p.W;
            screenPos.X = viewportPos.Value.X + (p.X + 1.0f) * 0.5f * viewportSize.Value.X;
            screenPos.Y = viewportPos.Value.Y + (1.0f - p.Y) * 0.5f * viewportSize.Value.Y;
            onScreen = true;
        }
      } else if (_gameGui != null) {
        onScreen = _gameGui.WorldToScreen(worldPos, out screenPos);
      } else {
        return false;
      }
      
      isBehind = !onScreen;

      // Clamp to prevent extreme coordinates
      var viewport = ImGui.GetMainViewport();
      float maxRange = MathF.Max(viewport.Size.X, viewport.Size.Y) * 2f;
      screenPos.X = Math.Clamp(screenPos.X, -maxRange, maxRange);
      screenPos.Y = Math.Clamp(screenPos.Y, -maxRange, maxRange);

      return true;
    }

    public void PlaceAt(Vector3 position, Vector3 lookAt) {
      _transform.PlaceLookingAt(position, lookAt);
      _transform.Enabled = true;
    }

    public void MoveBy(Vector3 offset) {
      _transform.Position += offset;
    }

    public void SetRotation(float yaw, float pitch, float roll = 0) {
      _transform.RotationDegrees = new Vector3(pitch, yaw, roll);
    }

    public void SetScale(float width, float height) {
      _transform.Scale = new Vector2(width, height);
    }

    public void Reset() {
      _transform.Enabled = false;
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;
      _depthRenderer?.Dispose();
      _glowRenderer?.Dispose();
      _vignetteExtractor?.Dispose();
      
      _gbuffer2Srv?.Dispose();
      _gbuffer3Srv?.Dispose();
      _unk68Srv?.Dispose();
      _unk68CopyTex?.Dispose();
    }
  }
}
















