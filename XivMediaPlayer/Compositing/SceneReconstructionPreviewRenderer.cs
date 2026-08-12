using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace XivMediaPlayer.Compositing {
  /// <summary>
  /// Renders a reconstructed scene from FFXIV's GBuffers for preview in the UI.
  /// </summary>
  internal unsafe class SceneReconstructionPreviewRenderer : IDisposable {
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;

    // Shader pipeline
    private ID3D11VertexShader _vertexShader;
    private ID3D11PixelShader _pixelShader;
    private ID3D11SamplerState _sampler;
    private ID3D11BlendState _blendState;

    // Output texture
    private ID3D11Texture2D _previewTexture;
    private ID3D11ShaderResourceView _previewSRV;
    private ID3D11RenderTargetView _previewRTV;
    private int _width;
    private int _height;

    private ID3D11Texture2D _copyTex0;
    private ID3D11Texture2D _copyTex1;
    private ID3D11Texture2D _copyTex2;
    private ID3D11Texture2D _copyTex3;
    private ID3D11Texture2D _copyTex4;
    private ID3D11Texture2D _copyTexDiff;
    private ID3D11Texture2D _copyTexSpec;
    private ID3D11Texture2D _copyTexUnk;

    private bool _initialized;
    private bool _disposed;

    private const string PreviewShaderCode = @"
Texture2D GBuffer2 : register(t0);
Texture2D LightDiffuse : register(t1);
Texture2D LightSpecular : register(t2);
Texture2D GBuffer4 : register(t3);
Texture2D BackBuffer : register(t4);
Texture2D Unk68 : register(t5);

SamplerState LinearSampler : register(s0);

cbuffer SettingsBuffer : register(b0) {
    int ShowMode; // 0 = Reconstructed, 1 = Diff Map, 2 = Alpha Mask
    float3 Padding;
};

struct VS_OUT {
  float4 pos : SV_POSITION;
  float2 uv : TEXCOORD;
};

VS_OUT VS(uint id : SV_VertexID) {
  VS_OUT o;
  o.uv = float2((id << 1) & 2, id & 2);
  o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
  return o;
}

float4 PS(VS_OUT input) : SV_TARGET {
  float4 albedo = GBuffer2.Sample(LinearSampler, input.uv);
  
  // Read actual engine lighting buffers!
  float3 diffuseLight = LightDiffuse.Sample(LinearSampler, input.uv).rgb;
  float3 specularLight = LightSpecular.Sample(LinearSampler, input.uv).rgb;
  float4 gbuffer4 = GBuffer4.Sample(LinearSampler, input.uv);
  float4 unk68 = Unk68.Sample(LinearSampler, input.uv);
  
  // The user confirmed that Unk68 is the 1:1 final tonemapped, gamma-corrected game scene
  // right before the UI is drawn!
  // Therefore, no manual reconstruction, tonemapping, or gamma correction is needed.
  float3 color = unk68.rgb;
  
  float3 bbColor = BackBuffer.Sample(LinearSampler, input.uv).rgb;
  float3 trueBackground = color.rgb;

  if (ShowMode == 1) {
      // Diff Map (RGB)
      color = abs(bbColor - trueBackground);
  } else if (ShowMode == 2) {
      // Raw Alpha Mask View
      float diffR = abs(bbColor.r - trueBackground.r);
      float diffG = abs(bbColor.g - trueBackground.g);
      float diffB = abs(bbColor.b - trueBackground.b);
      float ltcCenter = (0.299 * diffR) + (0.587 * diffG) + (0.114 * diffB);
      
      float nativeAlpha = BackBuffer.Sample(LinearSampler, input.uv).a;
      float unk68Alpha = Unk68.Sample(LinearSampler, input.uv).a;
      float alphaDiff = abs(nativeAlpha - unk68Alpha);
      
      float4 albedo = GBuffer2.Sample(LinearSampler, input.uv);
      bool isSkybox = (dot(albedo.rgb, albedo.rgb) < 0.0001);
      
      float trueAlpha = 0.0;
      // Same soft smoothstep band for the raw alpha mask view
      if (isSkybox) {
          trueAlpha = smoothstep(0.005, 0.3, ltcCenter);
      } else {
          trueAlpha = saturate(max(smoothstep(0.005, 0.3, ltcCenter), alphaDiff));
      }
      
      // If the center is literally just black sky, force it to 0 regardless of neighbors
      if (ltcCenter < 0.005) trueAlpha = 0.0;
      
      color = float3(trueAlpha, trueAlpha, trueAlpha);
  } else if (ShowMode == 3) {
      // Inverted Difference Map Experiment (Unmasked!)
      color = saturate(1.0 - abs(bbColor - trueBackground));
  } else if (ShowMode == 4) {
      // Native SwapChain Alpha
      float a = BackBuffer.Sample(LinearSampler, input.uv).a;
      color = float3(a, a, a);
  } else if (ShowMode == 5) {
      // Alpha Difference Map (The Holy Grail?)
      float nativeAlpha = BackBuffer.Sample(LinearSampler, input.uv).a;
      float unk68Alpha = Unk68.Sample(LinearSampler, input.uv).a;
      float alphaDiff = abs(nativeAlpha - unk68Alpha);
      color = float3(alphaDiff, alphaDiff, alphaDiff);
  } else if (ShowMode == 6) {
      // Literal Inward-Eroding Distance Transform (Tight Inner Feather)
      // This strictly erodes the alpha inwards towards the core, never expanding outwards!
      float2 texel = float2(1.0 / 1920.0, 1.0 / 1080.0);
      float maxRadius = 8.0;
      float minDist = maxRadius;
      
      // Check if the current center pixel is background. If so, it stays 0.0.
      float diffR = abs(bbColor.r - trueBackground.r);
      float diffG = abs(bbColor.g - trueBackground.g);
      float diffB = abs(bbColor.b - trueBackground.b);
      float centerG = (0.299 * diffR) + (0.587 * diffG) + (0.114 * diffB);
      
      float featheredAlpha = 0.0;
      
      if (centerG > 0.0196) {
          // It's a UI pixel! Now we search the 17x17 neighborhood for the NEAREST background pixel.
          int blackCount = 0;
          
          for (int y = -8; y <= 8; y++) {
              for (int x = -8; x <= 8; x++) {
                  float dist = length(float2(x, y));
                  if (dist <= maxRadius) {
                      float3 b = BackBuffer.Sample(LinearSampler, input.uv + float2(x * texel.x, y * texel.y)).rgb;
                      float3 u = Unk68.Sample(LinearSampler, input.uv + float2(x * texel.x, y * texel.y)).rgb;
                      float3 d = abs(b - u);
                      float g = (0.299 * d.r) + (0.587 * d.g) + (0.114 * d.b);
                      
                      // If we found a background pixel, record its distance!
                      if (g <= 0.0196) {
                          blackCount++;
                          if (dist < minDist) {
                              minDist = dist;
                          }
                      }
                  }
              }
          }
          
          // Noise Protection: If there are less than 5 background pixels in the massive 17x17 area,
          // it's just visual noise deep inside the solid UI mask. Force it to be completely solid!
          if (blackCount < 5) {
              featheredAlpha = 1.0;
          } else {
              // The closer we are to the true background edge, the lower the alpha.
              // This creates a perfectly tight, linear inward erode!
              featheredAlpha = minDist / maxRadius;
          }
      }
      
      return float4(bbColor, featheredAlpha);
  } else {
      // Standard Reconstructed Scene (Without UI)
      // 'color' is already unk68.rgb (trueBackground), so we just leave it alone
      // rather than mathematically adding the UI back into it!
  }
  
  return float4(color, 1.0);
}
";

    private ID3D11Buffer _constantBuffer;
    public int ShowMode { get; set; } = 0;

    public IntPtr PreviewTextureHandle => _previewSRV?.NativePointer ?? IntPtr.Zero;
    public ID3D11ShaderResourceView PreviewSRV => _previewSRV;
    public bool IsInitialized => _initialized;

    public bool Initialize(int width = 1920, int height = 1080) {
      if (_initialized || _disposed) return _initialized;

      try {
        var ffxivDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null)
          return false;

        var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
        System.Runtime.InteropServices.Marshal.AddRef(contextPtr);
        _context = new ID3D11DeviceContext(contextPtr);
        _device = _context.Device;
        _width = width;
        _height = height;

        // Compile shaders
        if (!ShaderCompileHelper.TryCompile(PreviewShaderCode, "VS", "SceneReconstructionPreviewRenderer.hlsl", "vs_5_0", out ReadOnlyMemory<byte> vsBytecode, out string shaderError)) {
          return false;
        }
        _vertexShader = _device.CreateVertexShader(vsBytecode.Span);

        if (!ShaderCompileHelper.TryCompile(PreviewShaderCode, "PS", "SceneReconstructionPreviewRenderer.hlsl", "ps_5_0", out ReadOnlyMemory<byte> psBytecode, out shaderError)) {
          return false;
        }
        _pixelShader = _device.CreatePixelShader(psBytecode.Span);

        _sampler = _device.CreateSamplerState(new SamplerDescription {
          Filter = Filter.MinMagMipLinear,
          AddressU = TextureAddressMode.Clamp,
          AddressV = TextureAddressMode.Clamp,
          AddressW = TextureAddressMode.Clamp
        });

        var cbDesc = new BufferDescription {
            ByteWidth = 16, // Float4
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        };
        _constantBuffer = _device.CreateBuffer(cbDesc);

        var blendDesc = new BlendDescription();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription {
          BlendEnable = false,
          RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendState = _device.CreateBlendState(blendDesc);

        _previewTexture = _device.CreateTexture2D(new Texture2DDescription {
          Width = _width,
          Height = _height,
          MipLevels = 1,
          ArraySize = 1,
          Format = Format.R8G8B8A8_UNorm,
          SampleDescription = new SampleDescription(1, 0),
          Usage = ResourceUsage.Default,
          BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
          CPUAccessFlags = CpuAccessFlags.None,
        });

        _previewRTV = _device.CreateRenderTargetView(_previewTexture);
        _previewSRV = _device.CreateShaderResourceView(_previewTexture);

        _initialized = true;
        return true;
      } catch {
        return false;
      }
    }

    private ID3D11ShaderResourceView CreateSRVSafe(ID3D11Texture2D sourceTex, ref ID3D11Texture2D cacheTex) {
        var desc = sourceTex.Description;
        if ((desc.BindFlags & BindFlags.ShaderResource) != 0) {
            return _device.CreateShaderResourceView(sourceTex);
        }
        
        if (cacheTex == null || cacheTex.Description.Width != desc.Width || cacheTex.Description.Height != desc.Height || cacheTex.Description.Format != desc.Format) {
            cacheTex?.Dispose();
            var copyDesc = desc;
            copyDesc.BindFlags = BindFlags.ShaderResource;
            copyDesc.Usage = ResourceUsage.Default;
            copyDesc.CPUAccessFlags = CpuAccessFlags.None;
            copyDesc.MiscFlags = ResourceOptionFlags.None;
            cacheTex = _device.CreateTexture2D(copyDesc);
        }
        
        if (desc.SampleDescription.Count > 1) {
            _context.ResolveSubresource(cacheTex, 0, sourceTex, 0, desc.Format);
        } else {
            _context.CopyResource(cacheTex, sourceTex);
        }
        
        return _device.CreateShaderResourceView(cacheTex);
    }

    public unsafe bool Update(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* gb0, FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* gb1, FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* gb2, FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* gb3, FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* gb4, FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* unk68, FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* lightDiffuse, FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* lightSpecular, ID3D11ShaderResourceView bbSrv) {
      if (!_initialized || _disposed || gb0 == null || gb1 == null || gb2 == null || gb3 == null || gb4 == null || unk68 == null || lightDiffuse == null || lightSpecular == null || bbSrv == null ||
          gb0->D3D11Texture2D == null || gb1->D3D11Texture2D == null || gb2->D3D11Texture2D == null || gb3->D3D11Texture2D == null || gb4->D3D11Texture2D == null || unk68->D3D11Texture2D == null || lightDiffuse->D3D11Texture2D == null || lightSpecular->D3D11Texture2D == null) return false;

      ID3D11ShaderResourceView srv0 = null;
      ID3D11ShaderResourceView srv1 = null;
      ID3D11ShaderResourceView srv2 = null;
      ID3D11ShaderResourceView srv3 = null;
      ID3D11ShaderResourceView srv4 = null;
      ID3D11ShaderResourceView lightDiffuseSrv = null;
      ID3D11ShaderResourceView lightSpecularSrv = null;
      ID3D11ShaderResourceView unk68Srv = null;

      try {
        var texPtr0 = (IntPtr)gb0->D3D11Texture2D;
        var texPtr1 = (IntPtr)gb1->D3D11Texture2D;
        var texPtr2 = (IntPtr)gb2->D3D11Texture2D;
        var texPtr3 = (IntPtr)gb3->D3D11Texture2D;
        var texPtr4 = (IntPtr)gb4->D3D11Texture2D;
        var texPtrDiff = (IntPtr)lightDiffuse->D3D11Texture2D;
        var texPtrSpec = (IntPtr)lightSpecular->D3D11Texture2D;
        var texPtrUnk = (IntPtr)unk68->D3D11Texture2D;
        
        System.Runtime.InteropServices.Marshal.AddRef(texPtr0);
        using var tex0 = new ID3D11Texture2D(texPtr0);
        System.Runtime.InteropServices.Marshal.AddRef(texPtr1);
        using var tex1 = new ID3D11Texture2D(texPtr1);
        System.Runtime.InteropServices.Marshal.AddRef(texPtr2);
        using var tex2 = new ID3D11Texture2D(texPtr2);
        System.Runtime.InteropServices.Marshal.AddRef(texPtr3);
        using var tex3 = new ID3D11Texture2D(texPtr3);
        System.Runtime.InteropServices.Marshal.AddRef(texPtr4);
        using var tex4 = new ID3D11Texture2D(texPtr4);
        System.Runtime.InteropServices.Marshal.AddRef(texPtrDiff);
        using var texDiff = new ID3D11Texture2D(texPtrDiff);
        System.Runtime.InteropServices.Marshal.AddRef(texPtrSpec);
        using var texSpec = new ID3D11Texture2D(texPtrSpec);
        System.Runtime.InteropServices.Marshal.AddRef(texPtrUnk);
        using var texUnk = new ID3D11Texture2D(texPtrUnk);

        srv0 = CreateSRVSafe(tex0, ref _copyTex0);
        srv1 = CreateSRVSafe(tex1, ref _copyTex1);
        srv2 = CreateSRVSafe(tex2, ref _copyTex2);
        srv3 = CreateSRVSafe(tex3, ref _copyTex3);
        srv4 = CreateSRVSafe(tex4, ref _copyTex4);
        lightDiffuseSrv = CreateSRVSafe(texDiff, ref _copyTexDiff);
        lightSpecularSrv = CreateSRVSafe(texSpec, ref _copyTexSpec);
        unk68Srv = CreateSRVSafe(texUnk, ref _copyTexUnk);

        var savedRTVs = new ID3D11RenderTargetView[1];
        ID3D11DepthStencilView savedDSV;
        _context.OMGetRenderTargets(1, savedRTVs, out savedDSV);

        try {
          _context.ClearRenderTargetView(_previewRTV, new Vortice.Mathematics.Color4(0, 0, 0, 0));
          _context.OMSetRenderTargets(_previewRTV);
          _context.RSSetViewport(0, 0, _width, _height);
          _context.OMSetBlendState(_blendState);

          var mapped = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
          if (mapped.DataPointer != IntPtr.Zero)
          {
              int* data = (int*)mapped.DataPointer;
              data[0] = ShowMode;
              data[1] = 0;
              data[2] = 0;
              data[3] = 0;
              _context.Unmap(_constantBuffer, 0);
          }

          _context.PSSetConstantBuffer(0, _constantBuffer);

          _context.PSSetShaderResource(0, srv2);
          _context.PSSetShaderResource(1, lightDiffuseSrv);
          _context.PSSetShaderResource(2, lightSpecularSrv);
          _context.PSSetShaderResource(3, srv4);
          _context.PSSetShaderResource(4, bbSrv);
          _context.PSSetShaderResource(5, unk68Srv);
          _context.PSSetSampler(0, _sampler);

          _context.VSSetShader(_vertexShader);
          _context.PSSetShader(_pixelShader);

          _context.IASetInputLayout(null);
          _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
          _context.Draw(3, 0);

          return true;
        } finally {
          _context.OMSetRenderTargets(savedRTVs, savedDSV);
        }
      } catch (Exception ex) {
        System.IO.File.WriteAllText(@"C:\Users\stel9\Documents\UpdateError.txt", ex.ToString());
        return false;
      } finally {
        srv0?.Dispose();
        srv1?.Dispose();
        srv2?.Dispose();
        srv3?.Dispose();
        srv4?.Dispose();
        lightDiffuseSrv?.Dispose();
        lightSpecularSrv?.Dispose();
        unk68Srv?.Dispose();
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;

      _copyTex0?.Dispose();
      _copyTex1?.Dispose();
      _copyTex2?.Dispose();
      _copyTex3?.Dispose();
      _copyTex4?.Dispose();
      _copyTexDiff?.Dispose();
      _copyTexSpec?.Dispose();
      _copyTexUnk?.Dispose();

      _constantBuffer?.Dispose();
      _previewRTV?.Dispose();
      _previewSRV?.Dispose();
      _previewTexture?.Dispose();
      _blendState?.Dispose();
      _sampler?.Dispose();
      _pixelShader?.Dispose();
      _vertexShader?.Dispose();
      _device?.Dispose();
      _context?.Dispose();
      _device = null;
      _context = null;
    }
  }
}
