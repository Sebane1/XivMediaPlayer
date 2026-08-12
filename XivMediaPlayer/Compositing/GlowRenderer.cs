using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace XivMediaPlayer.Compositing {
  /// <summary>
  /// Creates a small, vignette-faded copy of the video texture on the GPU.
  /// The shader downsamples and applies a smooth rectangular falloff so
  /// edges fade to transparent. No hard edges when drawn as a glow quad.
  /// </summary>
  internal unsafe class GlowRenderer : IDisposable {
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;

    // Shader pipeline
    private ID3D11VertexShader _vertexShader;
    private ID3D11PixelShader _pixelShader;
    private ID3D11SamplerState _sampler;
    private ID3D11BlendState _blendState;

    // Output glow texture
    private ID3D11Texture2D _glowTexture;
    private ID3D11ShaderResourceView _glowSRV;
    private ID3D11RenderTargetView _glowRTV;
    private int _glowSize;

    private bool _initialized;
    private bool _disposed;

    private const string GlowShaderCode = @"
Texture2D VideoTexture : register(t0);
SamplerState VideoSampler : register(s0);

struct VS_OUT {
  float4 pos : SV_POSITION;
  float2 uv : TEXCOORD;
};

// Fullscreen triangle trick. No vertex buffer needed.
VS_OUT VS(uint id : SV_VertexID) {
  VS_OUT o;
  o.uv = float2((id << 1) & 2, id & 2);
  o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
  return o;
}

float4 PS(VS_OUT input) : SV_TARGET {
  float4 color = 0;
  float2 offset = float2(0.04, 0.04);
  
  // 9-tap scattered blur
  color += VideoTexture.Sample(VideoSampler, input.uv);
  color += VideoTexture.Sample(VideoSampler, input.uv + float2(offset.x, offset.y));
  color += VideoTexture.Sample(VideoSampler, input.uv + float2(-offset.x, offset.y));
  color += VideoTexture.Sample(VideoSampler, input.uv + float2(offset.x, -offset.y));
  color += VideoTexture.Sample(VideoSampler, input.uv + float2(-offset.x, -offset.y));
  color += VideoTexture.Sample(VideoSampler, input.uv + float2(0, offset.y));
  color += VideoTexture.Sample(VideoSampler, input.uv + float2(0, -offset.y));
  color += VideoTexture.Sample(VideoSampler, input.uv + float2(offset.x, 0));
  color += VideoTexture.Sample(VideoSampler, input.uv + float2(-offset.x, 0));
  
  color /= 9.0;

  // Rectangular vignette: smooth fade to 0 at edges
  float2 d = abs(input.uv - 0.5) * 2.0; // 0 at center, 1 at edge
  // Smooth cubic falloff per axis
  float fx = saturate(1.0 - d.x * d.x * d.x);
  float fy = saturate(1.0 - d.y * d.y * d.y);
  float falloff = fx * fy;

  color.rgb *= falloff;
  color.a = falloff;
  return color;
}
";

    /// <summary>
    /// The SRV pointer for use with ImGui.AddImageQuad.
    /// </summary>
    public IntPtr GlowTextureHandle => _glowSRV?.NativePointer ?? IntPtr.Zero;
    public bool IsInitialized => _initialized;

    public bool Initialize(int glowSize = 64) {
      if (_initialized || _disposed) return _initialized;

      try {
        var ffxivDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null)
          return false;

        var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
        System.Runtime.InteropServices.Marshal.AddRef(contextPtr); // prevent Release on GC
        _context = new ID3D11DeviceContext(contextPtr);
        System.Runtime.InteropServices.Marshal.AddRef(_context.Device.NativePointer);
        _device = _context.Device;
        _glowSize = glowSize;

        // Compile shaders
        if (!ShaderCompileHelper.TryCompile(GlowShaderCode, "VS", "GlowRenderer.hlsl", "vs_5_0", out ReadOnlyMemory<byte> vsBytecode, out _)) {
          return false;
        }
        _vertexShader = _device.CreateVertexShader(vsBytecode.Span);

        if (!ShaderCompileHelper.TryCompile(GlowShaderCode, "PS", "GlowRenderer.hlsl", "ps_5_0", out ReadOnlyMemory<byte> psBytecode, out _)) {
          return false;
        }
        _pixelShader = _device.CreatePixelShader(psBytecode.Span);

        // Linear sampler for smooth downsampling
        _sampler = _device.CreateSamplerState(new SamplerDescription {
          Filter = Filter.MinMagMipLinear,
          AddressU = TextureAddressMode.Clamp,
          AddressV = TextureAddressMode.Clamp,
          AddressW = TextureAddressMode.Clamp,
        });

        // Blend state: just write directly (premultiplied alpha in texture)
        var blendDesc = new BlendDescription();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription {
          BlendEnable = false,
          RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendState = _device.CreateBlendState(blendDesc);

        // Create output texture
        _glowTexture = _device.CreateTexture2D(new Texture2DDescription {
          Width = _glowSize,
          Height = _glowSize,
          MipLevels = 1,
          ArraySize = 1,
          Format = Format.R8G8B8A8_UNorm,
          SampleDescription = new SampleDescription(1, 0),
          Usage = ResourceUsage.Default,
          BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
          CPUAccessFlags = CpuAccessFlags.None,
        });

        _glowRTV = _device.CreateRenderTargetView(_glowTexture);
        _glowSRV = _device.CreateShaderResourceView(_glowTexture);

        _initialized = true;
        return true;
      } catch {
        return false;
      }
    }

    /// <summary>
    /// Renders the video texture into the glow texture with vignette falloff.
    /// The GPU does the downsampling (bilinear) and edge fading in one pass.
    /// </summary>
    public bool UpdateFromVideoTexture(IntPtr videoSrvPtr) {
      if (!_initialized || _disposed || videoSrvPtr == IntPtr.Zero) return false;

      try {
        // Save current state
        var savedRTVs = new ID3D11RenderTargetView[1];
        ID3D11DepthStencilView savedDSV;
        _context.OMGetRenderTargets(1, savedRTVs, out savedDSV);

        try {
          // Set our tiny render target
          _context.ClearRenderTargetView(_glowRTV, new Vortice.Mathematics.Color4(0, 0, 0, 0));
          _context.OMSetRenderTargets(_glowRTV);
          _context.RSSetViewport(0, 0, _glowSize, _glowSize);
          _context.OMSetBlendState(_blendState);

          // Bind video texture as input
          System.Runtime.InteropServices.Marshal.AddRef(videoSrvPtr);
          using var videoSRV = new ID3D11ShaderResourceView(videoSrvPtr);
          _context.PSSetShaderResource(0, videoSRV);
          _context.PSSetSampler(0, _sampler);

          // Set shaders
          _context.VSSetShader(_vertexShader);
          _context.PSSetShader(_pixelShader);

          // Draw fullscreen triangle (3 verts, no vertex buffer needed)
          _context.IASetInputLayout(null);
          _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
          _context.Draw(3, 0);

          return true;
        } finally {
          // Restore
          _context.OMSetRenderTargets(savedRTVs, savedDSV);
          _context.PSSetShaderResource(0, (ID3D11ShaderResourceView)null);
        }
      } catch {
        return false;
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;

      _glowRTV?.Dispose();
      _glowSRV?.Dispose();
      _glowTexture?.Dispose();
      _blendState?.Dispose();
      _sampler?.Dispose();
      _pixelShader?.Dispose();
      _vertexShader?.Dispose();
    }
  }
}
