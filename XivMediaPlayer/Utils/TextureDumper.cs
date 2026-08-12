using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace XivMediaPlayer.Utils {
    internal class TextureDumper : IDisposable {
        private ID3D11Device _device;
        private ID3D11DeviceContext _context;
        private ID3D11VertexShader _vertexShader;
        private ID3D11PixelShader _pixelShader;
        private ID3D11SamplerState _sampler;
        private ID3D11BlendState _blendState;

        private bool _initialized;
        private bool _disposed;

        private const string ShaderCode = @"
Texture2D SourceTex : register(t0);
SamplerState LinearSampler : register(s0);

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
  return SourceTex.Sample(LinearSampler, input.uv);
}
";

        public unsafe bool Initialize() {
            if (_initialized) return true;
            try {
                var ffxivDevice = Device.Instance();
                if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null) return false;

                var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
                Marshal.AddRef(contextPtr);
                _context = new ID3D11DeviceContext(contextPtr);
                _device = _context.Device;

                if (!Compositing.ShaderCompileHelper.TryCompile(ShaderCode, "VS", "TextureDumper.hlsl", "vs_5_0", out ReadOnlyMemory<byte> vsBytecode, out _)) {
                    return false;
                }
                _vertexShader = _device.CreateVertexShader(vsBytecode.Span);

                if (!Compositing.ShaderCompileHelper.TryCompile(ShaderCode, "PS", "TextureDumper.hlsl", "ps_5_0", out ReadOnlyMemory<byte> psBytecode, out _)) {
                    return false;
                }
                _pixelShader = _device.CreatePixelShader(psBytecode.Span);

                _sampler = _device.CreateSamplerState(new SamplerDescription {
                    Filter = Filter.MinMagMipPoint, // Point filtering so we get exact pixels
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                });

                var blendDesc = new BlendDescription();
                blendDesc.RenderTarget[0] = new RenderTargetBlendDescription {
                    BlendEnable = false,
                    RenderTargetWriteMask = ColorWriteEnable.All,
                };
                _blendState = _device.CreateBlendState(blendDesc);

                _initialized = true;
                return true;
            } catch {
                return false;
            }
        }

        public unsafe byte[] DumpTextureToRgba(ID3D11ShaderResourceView srv, int width, int height) {
            if (!_initialized || _disposed || srv == null) return null;

            ID3D11Texture2D targetTex = null;
            ID3D11RenderTargetView rtv = null;
            ID3D11Texture2D stagingTex = null;

            try {
                targetTex = _device.CreateTexture2D(new Texture2DDescription {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None
                });

                rtv = _device.CreateRenderTargetView(targetTex);

                var savedRTVs = new ID3D11RenderTargetView[1];
                ID3D11DepthStencilView savedDSV;
                _context.OMGetRenderTargets(1, savedRTVs, out savedDSV);

                try {
                    _context.OMSetRenderTargets(rtv);
                    _context.RSSetViewport(0, 0, width, height);
                    _context.OMSetBlendState(_blendState);

                    _context.PSSetShaderResource(0, srv);
                    _context.PSSetSampler(0, _sampler);

                    _context.VSSetShader(_vertexShader);
                    _context.PSSetShader(_pixelShader);

                    _context.IASetInputLayout(null);
                    _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
                    _context.Draw(3, 0);
                } finally {
                    _context.OMSetRenderTargets(savedRTVs, savedDSV);
                    _context.PSSetShaderResource(0, (ID3D11ShaderResourceView)null);
                    savedRTVs[0]?.Dispose();
                    savedDSV?.Dispose();
                }

                stagingTex = _device.CreateTexture2D(new Texture2DDescription {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read
                });

                _context.CopyResource(stagingTex, targetTex);

                var mapped = _context.Map(stagingTex, 0, MapMode.Read);
                byte[] bgraData = new byte[width * height * 4];
                try {
                    for (int y = 0; y < height; y++) {
                        IntPtr rowSrc = new IntPtr(mapped.DataPointer.ToInt64() + y * mapped.RowPitch);
                        int rowDestOffset = y * width * 4;
                        Marshal.Copy(rowSrc, bgraData, rowDestOffset, width * 4);
                    }

                    // Convert RGBA to BGRA for DIB
                    for (int i = 0; i < bgraData.Length; i += 4) {
                        byte r = bgraData[i];
                        byte b = bgraData[i + 2];
                        bgraData[i] = b;
                        bgraData[i + 2] = r;
                        // bgraData[i+3] is A, bgraData[i+1] is G
                    }
                } finally {
                    _context.Unmap(stagingTex, 0);
                }

                return bgraData;

            } catch {
                return null;
            } finally {
                targetTex?.Dispose();
                rtv?.Dispose();
                stagingTex?.Dispose();
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            _vertexShader?.Dispose();
            _pixelShader?.Dispose();
            _sampler?.Dispose();
            _blendState?.Dispose();
            _device?.Dispose();
            _context?.Dispose();
        }
    }
}
