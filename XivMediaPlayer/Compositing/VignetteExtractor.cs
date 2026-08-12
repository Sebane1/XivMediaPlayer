using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace XivMediaPlayer.Compositing {
    /// <summary>
    /// Dynamically extracts and extrapolates FFXIV's subtractive vignette
    /// to perfectly restore the background without inverted colors.
    /// </summary>
    internal unsafe class VignetteExtractor : IDisposable {
        private ID3D11Device _device;
        private ID3D11DeviceContext _context;

        // Pipeline
        private ID3D11VertexShader _vertexShader;
        private ID3D11PixelShader _extractShader;
        private ID3D11PixelShader _extrapolateShader;
        private ID3D11SamplerState _linearSampler;
        private ID3D11BlendState _blendState;

        // Pass 1: Raw extraction texture
        private ID3D11Texture2D _rawTexture;
        private ID3D11ShaderResourceView _rawSRV;
        private ID3D11RenderTargetView _rawRTV;

        // Pass 2: Extrapolated texture
        private ID3D11Texture2D _extrapTexture;
        private ID3D11ShaderResourceView _extrapSRV;
        private ID3D11RenderTargetView _extrapRTV;

        private int _width = 128;
        private int _height = 72;

        private bool _initialized;
        private bool _disposed;

        public ID3D11ShaderResourceView ExtrapolatedVignetteSRV => _extrapSRV;

        private const string ExtractorShaderCode = @"
Texture2D BackBuffer : register(t0);
Texture2D Unk68 : register(t1);
Texture2D DepthTex : register(t2);

SamplerState LinearSampler : register(s0);

struct VSInput {
    uint vertexID : SV_VertexID;
};

struct PSInput {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

PSInput VSMain(VSInput input) {
    PSInput output;
    float2 texcoord = float2((input.vertexID << 1) & 2, input.vertexID & 2);
    output.position = float4(texcoord * float2(2, -2) + float2(-1, 1), 0, 1);
    output.uv = texcoord;
    return output;
}

float4 PSExtract(PSInput input) : SV_Target {
    float bbAlpha = BackBuffer.Sample(LinearSampler, input.uv).a;
    float gameDepth = DepthTex.Sample(LinearSampler, input.uv).r;
    
    // Check if pixel is pure background (no UI) or skybox
    if (bbAlpha < 0.01 || gameDepth < 0.00001) {
        float3 bbColor = BackBuffer.Sample(LinearSampler, input.uv).rgb;
        float3 unk68Color = Unk68.Sample(LinearSampler, input.uv).rgb;
        
        // FFXIV vignette is a subtractive darkening
        float3 vignette = saturate(unk68Color - bbColor);
        
        // alpha = 1.0 means this is a valid extracted pixel
        return float4(vignette, 1.0);
    }
    
    // Invalid pixel (UI covers it). Alpha = 0.0.
    return float4(0, 0, 0, 0.0);
}

// Pass 2: Extrapolate invalid pixels by searching for the nearest valid pixel.
Texture2D RawVignette : register(t0);

float4 PSExtrapolate(PSInput input) : SV_Target {
    float4 center = RawVignette.Sample(LinearSampler, input.uv);
    if (center.a > 0.5) {
        return center;
    }
    
    // Get dimensions of the small texture
    float w, h;
    RawVignette.GetDimensions(w, h);
    float2 texelSize = float2(1.0 / w, 1.0 / h);
    
    // Search in an expanding spiral/box
    int maxRadius = 32; // Covers half the screen at 128x72
    for (int r = 1; r <= maxRadius; r++) {
        for (int i = -r; i <= r; i++) {
            for (int j = -r; j <= r; j++) {
                if (abs(i) == r || abs(j) == r) {
                    float2 sampleUV = input.uv + float2(i, j) * texelSize;
                    float4 s = RawVignette.SampleLevel(LinearSampler, sampleUV, 0);
                    if (s.a > 0.5) {
                        return float4(s.rgb, 1.0);
                    }
                }
            }
        }
    }
    
    return float4(0, 0, 0, 0);
}
";

        public bool Initialize() {
            if (_initialized || _disposed) return _initialized;

            try {
                var ffxivDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
                if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null) return false;

                var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
                System.Runtime.InteropServices.Marshal.AddRef(contextPtr);
                _context = new ID3D11DeviceContext(contextPtr);
                _device = _context.Device;

                if (!ShaderCompileHelper.TryCompile(ExtractorShaderCode, "VSMain", "VignetteExtractor.hlsl", "vs_5_0", out ReadOnlyMemory<byte> vsBlob, out string error)) {
                    System.Diagnostics.Debug.WriteLine(error);
                    return false;
                }
                _vertexShader = _device.CreateVertexShader(vsBlob.Span);

                if (!ShaderCompileHelper.TryCompile(ExtractorShaderCode, "PSExtract", "VignetteExtractor.hlsl", "ps_5_0", out ReadOnlyMemory<byte> psExBlob, out error)) {
                    System.Diagnostics.Debug.WriteLine(error);
                    return false;
                }
                _extractShader = _device.CreatePixelShader(psExBlob.Span);

                if (!ShaderCompileHelper.TryCompile(ExtractorShaderCode, "PSExtrapolate", "VignetteExtractor.hlsl", "ps_5_0", out ReadOnlyMemory<byte> psExtraBlob, out error)) {
                    System.Diagnostics.Debug.WriteLine(error);
                    return false;
                }
                _extrapolateShader = _device.CreatePixelShader(psExtraBlob.Span);

                _linearSampler = _device.CreateSamplerState(new SamplerDescription {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp
                });

                var blendDesc = new BlendDescription {
                    AlphaToCoverageEnable = false,
                    IndependentBlendEnable = false
                };
                blendDesc.RenderTarget[0].BlendEnable = false;
                blendDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteEnable.All;
                _blendState = _device.CreateBlendState(blendDesc);

                RecreateTextures();

                _initialized = true;
                return true;
            } catch (Exception ex) {
                ShaderCompileHelper.LogIssue?.Invoke("[shader] VignetteExtractor init failed: " + ex);
                return false;
            }
        }

        private void RecreateTextures() {
            _rawTexture?.Dispose();
            _rawSRV?.Dispose();
            _rawRTV?.Dispose();

            _extrapTexture?.Dispose();
            _extrapSRV?.Dispose();
            _extrapRTV?.Dispose();

            var desc = new Texture2DDescription {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R16G16B16A16_Float, // Float to prevent precision loss in subtract
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None
            };

            _rawTexture = _device.CreateTexture2D(desc);
            _rawSRV = _device.CreateShaderResourceView(_rawTexture);
            _rawRTV = _device.CreateRenderTargetView(_rawTexture);

            _extrapTexture = _device.CreateTexture2D(desc);
            _extrapSRV = _device.CreateShaderResourceView(_extrapTexture);
            _extrapRTV = _device.CreateRenderTargetView(_extrapTexture);
        }

        public void Update(ID3D11ShaderResourceView backBuffer, ID3D11ShaderResourceView unk68, ID3D11ShaderResourceView depthTex) {
            if (!_initialized || _disposed || backBuffer == null || unk68 == null || depthTex == null) return;

            // Backup state
            var oldRTVs = new ID3D11RenderTargetView[1];
            ID3D11DepthStencilView oldDSV;
            _context.OMGetRenderTargets(1, oldRTVs, out oldDSV);

            try {
                // Pass 1: Extract
                _context.OMSetRenderTargets(_rawRTV);
                _context.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, _width, _height));
                _context.OMSetBlendState(_blendState);
                
                _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
                _context.VSSetShader(_vertexShader);
                _context.PSSetShader(_extractShader);
                _context.PSSetSampler(0, _linearSampler);

                _context.PSSetShaderResource(0, backBuffer);
                _context.PSSetShaderResource(1, unk68);
                _context.PSSetShaderResource(2, depthTex);

                _context.Draw(3, 0);

                // Unbind inputs before Pass 2
                _context.PSSetShaderResource(0, null);
                _context.PSSetShaderResource(1, null);
                _context.PSSetShaderResource(2, null);

                // Pass 2: Extrapolate
                _context.OMSetRenderTargets(_extrapRTV);
                _context.PSSetShader(_extrapolateShader);
                _context.PSSetShaderResource(0, _rawSRV);

                _context.Draw(3, 0);

                // Unbind inputs
                _context.PSSetShaderResource(0, null);
                
            } finally {
                // Restore state
                _context.OMSetRenderTargets(oldRTVs, oldDSV);
                oldDSV?.Dispose();
                if (oldRTVs[0] != null) oldRTVs[0].Dispose();
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;

            _rawTexture?.Dispose();
            _rawSRV?.Dispose();
            _rawRTV?.Dispose();

            _extrapTexture?.Dispose();
            _extrapSRV?.Dispose();
            _extrapRTV?.Dispose();

            _vertexShader?.Dispose();
            _extractShader?.Dispose();
            _extrapolateShader?.Dispose();
            _linearSampler?.Dispose();
            _blendState?.Dispose();

            _context?.Dispose();
            _device?.Dispose();
        }
    }
}
