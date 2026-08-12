using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace XivMediaPlayer.Compositing {
  /// <summary>
  /// Renders a textured quad with per-pixel depth occlusion using a fullscreen
  /// shader pass.
  /// </summary>
  internal unsafe class DepthTestedRenderer : IDisposable {
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;

    // Shaders
    private ID3D11VertexShader _vertexShader;
    private ID3D11PixelShader _pixelShader;
    private ID3D11BlendState _blendState;
    private ID3D11SamplerState _videoSampler;
    private ID3D11SamplerState _depthSampler;

    // Buffers
    private ID3D11Buffer _constantBuffer;
    private ID3D11Buffer _uiRectBuffer;

    // Offscreen render target
    private ID3D11Texture2D _renderTarget;
    private ID3D11RenderTargetView _renderTargetView;
    private ID3D11ShaderResourceView _renderTargetSRV;
    private int _rtWidth, _rtHeight;

    private bool _initialized;
    private bool _disposed;
    private string _initError;

    [StructLayout(LayoutKind.Sequential)]
    private struct PSConstants {
      public Vector2 CornerTL;
      public Vector2 CornerTR;
      public Vector2 CornerBR;
      public Vector2 CornerBL;
      public Vector2 ScreenSize;
      public float NearPlane;
      public float FarPlane;
      public Vector4 CornerDepths; // TL, TR, BR, BL depths

      public Vector3 CameraPos;
      public float FovY;
      public Vector3 CameraForward;
      public float AspectRatio;
      public Vector3 CameraRight;
      public float _pad2;
      public Vector3 CameraUp;
      public float _pad3;

      public Vector3 CornerTL3D;
      public float _pad4;
      public Vector3 CornerTR3D;
      public float _pad5;
      public Vector3 CornerBL3D;
      public float VideoAspectRatio;
      
      // New fields appended at the end
      public Vector2 HoverUV;
      public float Progress;
      public float PlaybackState;
      public float DynamicMinDepth;
      public float DynamicMaxDepth;
      public float HasBackBuffer;
      public float IsLockedTV;
      public float Volume;
      public Vector2 RenderResolution;
      public float HasTitleTexture;
      public float IsLooping;
      public float IsShuffle;
      public float Time;
      public float ShowScreensaver;
      public float HasPreUI;
      public float UseDifferenceFallback;
      public float Opacity;
      public float IsProjectorMode;
      public Vector3 ScreensaverColor;
      public float ScreensaverStyle;
      public float UIBlendThreshold;
      public float UVBottomEdge;
      public float UVRightEdge;
      public float EnableTvGlow;
      public float BufferProgress;
      public float LoadingPulse;
      public float IsLoadingOverlay;
      public float HasIdleBranding;
      public float IdleBrandingAspect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UIConstants {
      public fixed float UIRects[256]; // 64 * 4 (x, y, w, h)
      public fixed float UIRectTypes[64]; // 0 = standard, 1 = MJI
      public int UIRectCount;
      public float _pad0;
      public float _pad1;
      public float _pad2;
    }

    private const string ShaderCode = @"
cbuffer Constants : register(b0) {
  float2 CornerTL;
  float2 CornerTR;
  float2 CornerBR;
  float2 CornerBL;
  float2 ScreenSize;
  float NearPlane;
  float FarPlane;
  float4 CornerDepths; // x=TL, y=TR, z=BR, w=BL

  float3 CameraPos;
  float FovY;
  float3 CameraForward;
  float AspectRatio;
  float3 CameraRight;
  float _pad2;
  float3 CameraUp;
  float _pad3;

  float3 CornerTL3D;
  float _pad4;
  float3 CornerTR3D;
  float _pad5;
  float3 CornerBL3D;
  float VideoAspectRatio;

  float2 HoverUV;
  float Progress;
  float PlaybackState;
  float DynamicMinDepth;
  float DynamicMaxDepth;
  float HasBackBuffer;
  float IsLockedTV;
  float Volume;
  float2 RenderResolution;
  float HasTitleTexture;
  float IsLooping;
  float IsShuffle;
  float Time;
  float ShowScreensaver;
  float HasPreUI;
  float UseDifferenceFallback;
  float Opacity;
  float IsProjectorMode;

  float3 ScreensaverColor;
  float ScreensaverStyle;
  float UIBlendThreshold;
  float UVBottomEdge;
  float UVRightEdge;
  float EnableTvGlow;
  float BufferProgress;
  float LoadingPulse;
  float IsLoadingOverlay;
  float HasIdleBranding;
  float IdleBrandingAspect;
};
  
  cbuffer UIConsts : register(b1) {
  float4 UIRects[64];
  float4 UIRectTypes[16]; // 64 floats packed into 16 vectors
  int UIRectCount;
  float3 _padUI;
};

Texture2D VideoTexture : register(t0);
Texture2D DepthTexture : register(t1);
Texture2D BackBufferTexture : register(t2);
Texture2D TitleTexture : register(t3);
Texture2D PreUITexture : register(t4);
Texture2D GBuffer2 : register(t5);
Texture2D GBuffer3 : register(t6);
Texture2D VignetteExtrapolatedTexture : register(t7);
Texture2D IdleBrandingTexture : register(t8);
SamplerState VideoSampler : register(s0);
SamplerState DepthSampler : register(s1);

struct VS_OUT {
  float4 pos : SV_POSITION;
  float2 uv : TEXCOORD;
};

bool DrawXMPLogo(float2 p) {
    float2 pText = float2(p.x + p.y * 0.35, p.y);
    bool draw = false;
    if (p.x > -2.0 && p.x < 2.0 && p.y > -0.7 && p.y < 0.7) {
        if (pText.x > -1.53 && pText.x < 1.25 && pText.y > -0.6 && pText.y < -0.45) draw = true;
        if (pText.x > -1.5 && pText.x < -0.5 && pText.y > -0.5 && pText.y < 0.2) {
            float dx1 = abs((pText.x + 1.0) - (pText.y + 0.15) * 1.2);
            float dx2 = abs((pText.x + 1.0) + (pText.y + 0.15) * 1.2);
            if (dx1 < 0.11 || dx2 < 0.11) draw = true;
        }
        if (pText.x > -0.6 && pText.x < 0.6 && pText.y > -0.5) {
            if (pText.y < 0.2) {
                if (abs(pText.x + 0.4) < 0.11) draw = true;
                if (abs(pText.x - 0.4) < 0.11) draw = true;
            }
            float dm1 = abs(pText.x - (pText.y * 0.4 - 0.2));
            float dm2 = abs(pText.x - (-pText.y * 0.4 + 0.2));
            if (pText.x <= 0.0 && dm1 < 0.12 && pText.y < 0.55) draw = true;
            if (pText.x >= 0.0 && dm2 < 0.12 && pText.y < 0.55) draw = true;
        }
        if (pText.x > 0.5 && pText.x < 1.6 && pText.y > -0.61 && pText.y < 0.2) {
            if (abs(pText.x - 0.8) < 0.11) draw = true;
            if (pText.x >= 0.8 && pText.x <= 1.25) {
                if (abs(pText.y - (-0.05)) < 0.11) draw = true; 
            }
            if (pText.x > 1.25) {
                if (distance(float2(pText.x, pText.y), float2(1.25, -0.27)) < 0.33) {
                    if (distance(float2(pText.x, pText.y), float2(1.25, -0.305)) >= 0.145) {
                        draw = true;
                    }
                }
            }
        }
        if (distance(float2(p.x * 0.08, p.y - 0.45), float2(0,0)) < 0.12) {
            float dm1 = abs(pText.x - (pText.y * 0.5 - 0.25));
            float dm2 = abs(pText.x - (-pText.y * 0.5 + 0.25));
            float distToV = (pText.x < 0.0) ? dm1 : dm2;
            if (distToV > 0.16) {
                draw = true;
                if (p.x > -0.8 && p.x < 0.8 && abs(p.y - 0.45) < 0.025) draw = false;
            }
        }
        if (p.y < -0.6) draw = false;
    }
    return draw;
}

bool DrawLetter(float2 p, int letter) {
    if (p.x < 0.0 || p.x > 0.08 || p.y < 0.0 || p.y > 0.12) return false;
    
    if (letter == 0) { // P
        if (p.x < 0.02 || p.y < 0.02 || (p.y > 0.04 && p.y < 0.06) || (p.x > 0.06 && p.y < 0.05)) return true;
    } else if (letter == 1) { // L
        if (p.x < 0.02 || p.y > 0.1) return true;
    } else if (letter == 2) { // A
        if (p.x < 0.02 || p.x > 0.06 || p.y < 0.02 || (p.y > 0.04 && p.y < 0.06)) return true;
    } else if (letter == 3) { // Y
        if ((p.y < 0.06 && (p.x < 0.02 || p.x > 0.06)) || (p.y >= 0.05 && p.x > 0.03 && p.x < 0.05) || (p.y > 0.04 && p.y < 0.06)) return true;
    } else if (letter == 4) { // U
        if (p.x < 0.02 || p.x > 0.06 || p.y > 0.1) return true;
    } else if (letter == 5) { // S
        if (p.y < 0.02 || p.y > 0.1 || (p.y > 0.05 && p.y < 0.07) || (p.x < 0.02 && p.y < 0.06) || (p.x > 0.06 && p.y > 0.06)) return true;
    } else if (letter == 6) { // E
        if (p.x < 0.02 || p.y < 0.02 || p.y > 0.1 || (p.y > 0.05 && p.y < 0.07)) return true;
    } else if (letter == 7) { // T
        if (p.y < 0.02 || (p.x > 0.03 && p.x < 0.05)) return true;
    } else if (letter == 8) { // O
        if (p.x < 0.02 || p.x > 0.06 || p.y < 0.02 || p.y > 0.1) return true;
    } else if (letter == 9) { // N
        if (p.x < 0.02 || p.x > 0.06 || (p.y > p.x * 1.5 - 0.02 && p.y < p.x * 1.5 + 0.02)) return true;
    } else if (letter == 10) { // I
        if (p.y < 0.02 || p.y > 0.1 || (p.x > 0.03 && p.x < 0.05)) return true;
    } else if (letter == 11) { // G
        if (p.x < 0.02 || p.y < 0.02 || p.y > 0.1 || (p.x > 0.06 && p.y > 0.06) || (p.y > 0.05 && p.y < 0.07 && p.x > 0.04)) return true;
    }
    return false;
}

VS_OUT VS(uint id : SV_VertexID) {
  VS_OUT o;
  o.uv = float2((id << 1) & 2, id & 2);
  o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
  return o;
}



float4 PS(VS_OUT input) : SV_TARGET {
  float2 pixelPos = input.pos.xy;
  float2 screenUV = pixelPos / ScreenSize;

  float2 ndc = screenUV * 2.0 - 1.0;
  ndc.y = -ndc.y;

  float fovDist = 1.0 / tan(FovY * 0.5);
  float3 rayOrigin = CameraPos;
  // FFXIV uses a coordinate system where CameraForward (3rd column) points backwards from the camera.
  // We must negate it to point the ray towards the screen.
  float3 rayDir = normalize(ndc.x * AspectRatio * CameraRight + ndc.y * CameraUp - fovDist * CameraForward);

  float3 tvRight = CornerTR3D - CornerTL3D;
  float3 tvDown = CornerBL3D - CornerTL3D;
  float3 tvNormal = normalize(cross(tvDown, tvRight));

  float denom = dot(tvNormal, rayDir);
  bool isInside = false;
  float2 uv = float2(-1, -1);
  float t = -1.0;

  float2 sampleUV = float2(-1, -1);
  if (abs(denom) > 1e-6) {
      t = dot(CornerTL3D - rayOrigin, tvNormal) / denom;
      float3 hitPoint = rayOrigin + rayDir * t;
      float3 d = hitPoint - CornerTL3D;
      float u = dot(d, tvRight) / dot(tvRight, tvRight);
      float v = dot(d, tvDown) / dot(tvDown, tvDown);
      
      uv = float2(u, v);
      isInside = (t > 0.0 && u >= 0.0 && u <= 1.0 && v >= 0.0 && v <= 1.0);
      
      sampleUV = uv;
      if (VideoAspectRatio > 0) {
            float tvAspect = length(tvRight) / length(tvDown);
            float aspectDiff = abs(tvAspect - VideoAspectRatio) / max(tvAspect, VideoAspectRatio);
            if (aspectDiff > 0.005) {
                if (VideoAspectRatio > tvAspect) {
                    // Video is wider than the TV. Pillarbox (side bars).
                    float scale = tvAspect / VideoAspectRatio;
                    sampleUV.x = (sampleUV.x - 0.5) * scale + 0.5;
                } else {
                    // Video is taller than the TV. Letterbox (top/bottom bars).
                    float scale = VideoAspectRatio / tvAspect;
                    sampleUV.y = (sampleUV.y - 0.5) * scale + 0.5;
                }
            }
        }
        sampleUV.y = sampleUV.y * UVBottomEdge;
          sampleUV.x = sampleUV.x * UVRightEdge;
      }
  
  // Dynamic Resolution scaling: the depth buffer texture size might be larger than the actual rendered area
  float2 renderScale = float2(1.0, 1.0);
  float2 texelSize = 1.0 / ScreenSize;
  if (RenderResolution.x > 0 && RenderResolution.y > 0) {
      renderScale = RenderResolution / ScreenSize;
      texelSize = 1.0 / RenderResolution;
  }
  float2 depthUV = screenUV * renderScale;
  
  float gameDepth = DepthTexture.Sample(DepthSampler, depthUV).r;
  float4 color = float4(0, 0, 0, 0);

  float occlusion = 0.0;
  if (isInside) {
      // Calculate exact view-space Z distance of the intersection point
      float viewZ = dot(rayDir * t, -CameraForward);
      
      // Convert to non-linear reversed-Z depth to match FFXIV's depth buffer
      float exactDepth = 0.0;
      if (viewZ > 0.0) {
          exactDepth = NearPlane * (FarPlane - viewZ) / (viewZ * (FarPlane - NearPlane));
      }
      exactDepth = saturate(exactDepth);
      
      // Wide-radius soft-occlusion (PCF) with Gaussian weights
      float occlusionCount = 0.0;
      float totalWeight = 0.0;
      
      [unroll]
      for (int dy = -2; dy <= 2; dy++) {
          [unroll]
          for (int dx = -2; dx <= 2; dx++) {
              float d = DepthTexture.SampleLevel(DepthSampler, depthUV + float2(dx * texelSize.x * 1.5, dy * texelSize.y * 1.5), 0).r;
              float weight = exp(-0.3 * (dx*dx + dy*dy));
              if (d > exactDepth + 0.0001) occlusionCount += weight;
              totalWeight += weight;
          }
      }
      
      occlusion = occlusionCount / totalWeight;
      
      // Erode the occlusion mask to pull the TV pixels inwards, to cover the TAA blending.
      occlusion = smoothstep(0.55, 0.95, occlusion);
  }

  if (isInside && occlusion < 0.999) {
      // Draw unoccluded TV
      bool isOutOfBounds = (sampleUV.x < 0 || sampleUV.x > 1 || sampleUV.y < 0 || sampleUV.y > 1);
      
      if (isOutOfBounds && ShowScreensaver < 0.5) {
          color = float4(0, 0, 0, IsProjectorMode > 0.5 ? 0.0 : 1.0);
      } else {
          color = isOutOfBounds ? float4(0, 0, 0, 1.0) : VideoTexture.Sample(VideoSampler, sampleUV);
          
          // XMP Screensaver
          if (ShowScreensaver > 0.5) {
              color.rgb = ScreensaverColor; // Fill the backdrop
              float aspect = 16.0 / 9.0;
              if (RenderResolution.y > 0) aspect = RenderResolution.x / RenderResolution.y;

              if (HasIdleBranding > 0.5) {
                  float imgAspect = max(IdleBrandingAspect, 0.01);
                  float2 scale = float2(1.0, 1.0);
                  if (imgAspect > aspect) scale.y = aspect / imgAspect;
                  else scale.x = imgAspect / aspect;
                  float2 centered = (uv - 0.5) / scale + 0.5;
                  if (centered.x >= 0.0 && centered.x <= 1.0 && centered.y >= 0.0 && centered.y <= 1.0) {
                      float4 img = IdleBrandingTexture.Sample(VideoSampler, centered);
                      color.rgb = lerp(color.rgb, img.rgb, img.a);
                  }
              } else if (ScreensaverStyle < 0.5) {
                  // Style 0: Bouncing Logo
                  float speedX = 0.1;
                  float speedY = 0.075;
                  float bx = Time * speedX;
                  float by = Time * speedY;
                  
                  float logoSize = 0.15;
                  float logoW = logoSize * 3.0 / aspect;
                  float logoH = logoSize * 1.0;
                  
                  float rangeX = 1.0 - logoW;
                  float rangeY = 1.0 - logoH;
                  
                  float posX = (logoW / 2.0) + abs(fmod(bx, 2.0) - 1.0) * rangeX;
                  float posY = (logoH / 2.0) + abs(fmod(by, 2.0) - 1.0) * rangeY;
                  
                  float2 p = float2((uv.x - posX) * aspect / logoSize, (uv.y - posY) / logoSize);
                  bool draw = DrawXMPLogo(p);
                  
                  if (draw) {
                      int colorIdx = (int(floor(bx)) + int(floor(by))) % 6;
                      float3 logoColor = float3(1, 1, 1);
                      if (colorIdx == 0) logoColor = float3(1.0, 0.3, 0.3);
                      else if (colorIdx == 1) logoColor = float3(0.3, 1.0, 0.3);
                      else if (colorIdx == 2) logoColor = float3(0.3, 0.6, 1.0);
                      else if (colorIdx == 3) logoColor = float3(1.0, 1.0, 0.3);
                      else if (colorIdx == 4) logoColor = float3(1.0, 0.3, 1.0);
                      else if (colorIdx == 5) logoColor = float3(0.3, 1.0, 1.0);
                      
                      color.rgb = logoColor;
                  }
              } else if (ScreensaverStyle > 4.5) {
                  // Style 5: Matrix Rain
                  float2 screenUv = uv;
                  float aspect = 16.0 / 9.0;
                  if (RenderResolution.y > 0) aspect = RenderResolution.x / RenderResolution.y;
                  
                  // Setup grid (roughly 80 columns)
                  float cols = 80.0;
                  float rows = cols / aspect;
                  
                  float2 cell = floor(screenUv * float2(cols, rows));
                  float2 cellUv = frac(screenUv * float2(cols, rows));
                  
                  // Random hashes
                  float colHash = frac(sin(cell.x * 12.9898) * 43758.5453);
                  
                  // Column speed and drop offset
                  float speed = colHash * 0.5 + 0.3; // screen heights per second
                  float offset = frac(sin(cell.x * 78.233) * 43758.5453) * 100.0;
                  
                  // The continuous head of the drop for this column
                  float head = (Time * speed + offset) * rows; // in row units
                  
                  // Wrap distance from head
                  float dist = head - cell.y;
                  dist = fmod(dist, rows * 1.5);
                  if (dist < 0.0) dist += rows * 1.5;
                  
                  float tailLength = colHash * 15.0 + 10.0;
                  
                  float3 cellColor = float3(0.0, 0.0, 0.0);
                  
                  if (dist < tailLength) {
                      float brightness = 1.0 - (dist / tailLength);
                      brightness = max(0.0, brightness);
                      
                      // Head is bright/white, tail is green
                      float3 baseColor = (dist < 1.0) ? float3(0.6, 1.0, 0.6) : float3(0.0, brightness * 0.8, 0.0);
                      
                      // Draw random glyph
                      // Shrink UV to add padding between cells
                      float2 charUv = (cellUv - 0.15) / 0.7;
                      if (charUv.x >= 0.0 && charUv.x <= 1.0 && charUv.y >= 0.0 && charUv.y <= 1.0) {
                          // 3x4 pixel grid for characters
                          float gx = floor(charUv.x * 3.0);
                          float gy = floor(charUv.y * 4.0);
                          
                          // Change character periodically
                          float charTime = floor(Time * (colHash * 3.0 + 2.0));
                          float glyphSeed = frac(sin(dot(cell + charTime, float2(12.9898, 78.233))) * 43758.5453);
                          
                          // Hash the pixel to see if it's filled
                          float pixelHash = frac(sin(glyphSeed + gx * 13.0 + gy * 7.0) * 43758.5453);
                          if (pixelHash > 0.4) {
                              cellColor = baseColor;
                          }
                      }
                  }
                  
                  // Tint with ScreensaverColor if it is not black (allow color overriding)
                  float colorIntensity = length(ScreensaverColor);
                  if (colorIntensity > 0.05) {
                      // Apply custom color but keep brightness
                      float luma = max(cellColor.r, max(cellColor.g, cellColor.b));
                      cellColor = luma * normalize(ScreensaverColor) * 1.5;
                      if (dist < 1.0 && luma > 0.1) cellColor += float3(0.5, 0.5, 0.5); // Keep head somewhat white
                  }
                  
                  // Add subtle XMP Logo overlay
                  float logoScale = 0.15;
                  float2 logoP = float2((uv.x - 0.5) * aspect / logoScale, (uv.y - 0.5) / logoScale);
                  if (DrawXMPLogo(logoP)) {
                      cellColor = lerp(cellColor, float3(1.0, 1.0, 1.0), 0.3);
                  }
                  
                  color.rgb = cellColor;
              } else if (ScreensaverStyle > 3.5) {
                  // Style 4: Geometric Test Pattern
                  float2 centerUv = uv - 0.5;
                  
                  // Fix aspect ratio (TV is 16:9)
                  centerUv.x *= 1.7777777;
                  
                  // Base color white
                  float3 testColor = float3(1.0, 1.0, 1.0);
                  
                  // 1. Grid background
                  float gridX = abs(frac((centerUv.x + 0.5) * 8.0 + 0.5) - 0.5) * 16.0;
                  float gridY = abs(frac((centerUv.y + 0.5) * 8.0 + 0.5) - 0.5) * 16.0;
                  float grid = min(gridX, gridY);
                  // Dashed grid logic
                  float dashed = frac(centerUv.x * 40.0) < 0.5 && frac(centerUv.y * 40.0) < 0.5 ? 1.0 : 0.0;
                  if (grid < 0.03 && dashed > 0.5) testColor = float3(0.0, 0.0, 0.0);
                  if (grid < 0.01) testColor = float3(0.0, 0.0, 0.0); // thin solid core line
                  
                  float dist = length(centerUv);
                  
                  // 1b. Diagonal lines (X pattern) inside the large circle
                  // In the reference, they stop at the bullseye (dist > 0.25)
                  if (dist < 0.45 && dist > 0.25) {
                      float diag1 = abs(centerUv.x - centerUv.y);
                      float diag2 = abs(centerUv.x + centerUv.y);
                      if (diag1 < 0.003 || diag2 < 0.003) testColor = float3(0.0, 0.0, 0.0);
                  }
                  
                  // 2. Large Outer Circle
                  if (abs(dist - 0.45) < 0.006) testColor = float3(0.0, 0.0, 0.0);
                  if (abs(dist - 0.43) < 0.002) testColor = float3(0.0, 0.0, 0.0);
                  
                  // 3. Corner circles
                  // The corners in the original overlap the outer circle, so draw them AFTER the outer circle
                  float2 corners[4] = { float2(-0.35, -0.32), float2(0.35, -0.32), float2(-0.35, 0.32), float2(0.35, 0.32) };
                  for (int i = 0; i < 4; i++) {
                      float2 cornerUv = centerUv - corners[i];
                      float cDist = length(cornerUv);
                      if (cDist < 0.12) {
                          testColor = float3(1.0, 1.0, 1.0); // fill white
                          if (abs(cDist - 0.11) < 0.004) testColor = float3(0.0, 0.0, 0.0);
                          if (abs(cDist - 0.10) < 0.002) testColor = float3(0.0, 0.0, 0.0);
                          
                          // Crosshairs in corners
                          if (abs(cornerUv.x) < 0.003 && cDist < 0.1) testColor = float3(0.0, 0.0, 0.0);
                          if (abs(cornerUv.y) < 0.003 && cDist < 0.1) testColor = float3(0.0, 0.0, 0.0);
                          
                          // Inner tiny circle
                          if (cDist < 0.04) testColor = float3(1.0, 1.0, 1.0);
                          if (abs(cDist - 0.04) < 0.004) testColor = float3(0.0, 0.0, 0.0);
                          if (abs(cDist - 0.035) < 0.002) testColor = float3(0.0, 0.0, 0.0);
                      }
                  }
                  
                  // 2b. Bottom Horizontal Bars (Low Frequency Response)
                  if (centerUv.y > 0.28 && centerUv.y < 0.44 && abs(centerUv.x) < 0.25) {
                      if (centerUv.y < 0.29) {
                          testColor = float3(0.0, 0.0, 0.0); // Thick top bar
                      } else {
                          // 10 distinct rectangular bars
                          float barDist = (centerUv.y - 0.29) / 0.15; // 0 to 1
                          float barIndex = floor(barDist * 10.0);
                          float barWidth = lerp(0.25, 0.02, barIndex / 9.0);
                          if (abs(centerUv.x) < barWidth) {
                              float barY = frac(barDist * 10.0);
                              // Make the bar solid black with a little gap
                              if (barY < 0.5) testColor = float3(0.0, 0.0, 0.0);
                          }
                      }
                  }
                  
                  // 4. Center Bullseye and Wedges
                  if (dist < 0.25) {
                      testColor = float3(1.0, 1.0, 1.0); // fill white
                      
                      float angle = atan2(centerUv.y, centerUv.x); // -PI to PI
                      
                      // Vertical wedges bounded by V-shapes
                      if (abs(centerUv.x) < abs(centerUv.y) * 0.45 && dist > 0.09) {
                          // Use a lower frequency to get ~9 thick lines instead of 60 thin ones
                          float ray = sin(angle * 40.0);
                          if (ray > 0.0) testColor = float3(0.0, 0.0, 0.0);
                      }
                      
                      // Horizontal wedges bounded by V-shapes
                      if (abs(centerUv.y) < abs(centerUv.x) * 0.45 && dist > 0.09) {
                          float ray = sin(angle * 40.0);
                          if (ray > 0.0) testColor = float3(0.0, 0.0, 0.0);
                      }
                      
                      // Grayscale stepped wedges (Top-Left and Bottom-Right quadrants)
                      bool inTopLeft = centerUv.x < 0.0 && centerUv.y < 0.0 && abs(centerUv.x) > abs(centerUv.y) * 0.45 && abs(centerUv.y) > abs(centerUv.x) * 0.45;
                      if (inTopLeft && dist > 0.09 && dist < 0.25) {
                          // 6 distinct rings
                          float ringDist = floor((dist - 0.09) / 0.16 * 6.0);
                          float shade = ringDist / 5.0; // 0 to 1
                          testColor = float3(shade, shade, shade);
                          // Black border lines between rings
                          if (frac((dist - 0.09) / 0.16 * 6.0) < 0.1) testColor = float3(0.0, 0.0, 0.0);
                      }
                      
                      bool inBottomRight = centerUv.x > 0.0 && centerUv.y > 0.0 && abs(centerUv.x) > abs(centerUv.y) * 0.45 && abs(centerUv.y) > abs(centerUv.x) * 0.45;
                      if (inBottomRight && dist > 0.09 && dist < 0.25) {
                          float ringDist = floor((dist - 0.09) / 0.16 * 6.0);
                          float shade = 1.0 - (ringDist / 5.0); // 1 to 0
                          testColor = float3(shade, shade, shade);
                          if (frac((dist - 0.09) / 0.16 * 6.0) < 0.1) testColor = float3(0.0, 0.0, 0.0);
                      }
                      
                      // Center inner circles
                      // Thick outer ring of the center bullseye
                      if (abs(dist - 0.25) < 0.005) testColor = float3(0.0, 0.0, 0.0);
                      if (abs(dist - 0.23) < 0.002) testColor = float3(0.0, 0.0, 0.0);
                      
                      // Inner ring boundary around the checkerboard
                      if (dist < 0.09) testColor = float3(1.0, 1.0, 1.0);
                      if (abs(dist - 0.09) < 0.005) testColor = float3(0.0, 0.0, 0.0);
                      if (abs(dist - 0.08) < 0.002) testColor = float3(0.0, 0.0, 0.0);
                      
                      // XMP Logo target
                      if (dist < 0.078) {
                          float logoScale = 0.038;
                          float2 p = float2(centerUv.x / logoScale, centerUv.y / logoScale);
                          bool draw = DrawXMPLogo(p);
                          testColor = draw ? float3(0.0, 0.0, 0.0) : float3(1.0, 1.0, 1.0);
                      }
                  }
                  
                  // Tint with ScreensaverColor if it is not black
                  float colorIntensity = length(ScreensaverColor);
                  if (colorIntensity > 0.05) {
                      testColor = lerp(testColor, testColor * ScreensaverColor * 1.5, min(colorIntensity, 1.0));
                  }
                  
                  color.rgb = testColor;
              } else if (ScreensaverStyle > 2.5) {
                  // Style 3: TV Static
                  // Use frac(Time) to avoid huge float precision loss inside sin()
                  float timeVal = frac(Time) * 1000.0;
                  float2 noiseUv = floor(uv * float2(320.0, 240.0));
                  
                  // Simple pseudo-random
                  float random = frac(sin(dot(noiseUv + float2(timeVal, -timeVal), float2(12.9898, 78.233))) * 43758.5453);
                  
                  // Darken it slightly to look more like typical TV static (grayish rather than harsh white)
                  float luminance = lerp(0.1, 0.8, random);
                  float3 staticColor = float3(luminance, luminance, luminance);
                  
                  // Tint with ScreensaverColor if it is not black
                  float colorIntensity = length(ScreensaverColor);
                  if (colorIntensity > 0.05) {
                      staticColor = lerp(staticColor, staticColor * ScreensaverColor * 1.5, min(colorIntensity, 1.0));
                  }
                  
                  // Add subtle faint XMP Logo in center
                  float logoScale = 0.25;
                  float2 logoP = float2((uv.x - 0.5) * aspect / logoScale, (uv.y - 0.5) / logoScale);
                  if (DrawXMPLogo(logoP)) staticColor = lerp(staticColor, float3(1.0, 1.0, 1.0), 0.15);
                  
                  color.rgb = staticColor;
              } else if (ScreensaverStyle > 1.5) {
                  // Style 2: No Signal (SMPTE Color Bars)
                  
                  // Noise
                  float noise = frac(sin(dot(uv * Time, float2(12.9898, 78.233))) * 43758.5453);
                  float2 uvNoise = uv + float2(noise * 0.005, 0.0);
                  
                  // 7 Vertical Bars (Top 67%)
                  // White, Yellow, Cyan, Green, Magenta, Red, Blue
                  float3 bars[7];
                  bars[0] = float3(0.75, 0.75, 0.75);
                  bars[1] = float3(0.75, 0.75, 0.0);
                  bars[2] = float3(0.0, 0.75, 0.75);
                  bars[3] = float3(0.0, 0.75, 0.0);
                  bars[4] = float3(0.75, 0.0, 0.75);
                  bars[5] = float3(0.75, 0.0, 0.0);
                  bars[6] = float3(0.0, 0.0, 0.75);
                  
                  int barIndex = clamp(int(uvNoise.x * 7.0), 0, 6);
                  if (uvNoise.y < 0.67) {
                      color.rgb = bars[barIndex];
                  } else if (uvNoise.y < 0.75) {
                      // Middle small blocks
                      // Blue, Black, Magenta, Black, Cyan, Black, White
                      float3 midBars[7];
                      midBars[0] = float3(0.0, 0.0, 0.75);
                      midBars[1] = float3(0.0, 0.0, 0.0);
                      midBars[2] = float3(0.75, 0.0, 0.75);
                      midBars[3] = float3(0.0, 0.0, 0.0);
                      midBars[4] = float3(0.0, 0.75, 0.75);
                      midBars[5] = float3(0.0, 0.0, 0.0);
                      midBars[6] = float3(0.75, 0.75, 0.75);
                      color.rgb = midBars[barIndex];
                  } else {
                      // Bottom blocks
                      // -I, White, Q, Black
                      if (uvNoise.x < 0.18) color.rgb = float3(0.0, 0.2, 0.4);
                      else if (uvNoise.x < 0.35) color.rgb = float3(1.0, 1.0, 1.0);
                      else if (uvNoise.x < 0.53) color.rgb = float3(0.2, 0.0, 0.4);
                      else color.rgb = float3(0.0, 0.0, 0.0);
                  }
                  
                  // Add static/noise over everything
                  color.rgb += (noise - 0.5) * 0.15;
                  
                  // NO SIGNAL text box in center
                  float2 p = float2(uv.x * aspect - aspect * 0.5 + 0.45, uv.y - 0.45);
                  
                  // Black background box for NO SIGNAL
                  if (uv.x * aspect > aspect * 0.5 - 0.55 && uv.x * aspect < aspect * 0.5 + 0.55 && uv.y > 0.4 && uv.y < 0.6) {
                      color.rgb = float3(0.0, 0.0, 0.0);
                      
                      bool playDraw = false;
                      // N O   S I G N A L
                      if (DrawLetter(p, 9)) playDraw = true;
                      if (DrawLetter(p - float2(0.12, 0.0), 8)) playDraw = true;
                      
                      if (DrawLetter(p - float2(0.36, 0.0), 5)) playDraw = true;
                      if (DrawLetter(p - float2(0.48, 0.0), 10)) playDraw = true;
                      if (DrawLetter(p - float2(0.60, 0.0), 11)) playDraw = true;
                      if (DrawLetter(p - float2(0.72, 0.0), 9)) playDraw = true;
                      if (DrawLetter(p - float2(0.84, 0.0), 2)) playDraw = true;
                      if (DrawLetter(p - float2(0.96, 0.0), 1)) playDraw = true;
                      
                      if (playDraw) {
                          // Text has some chromatic aberration
                          color.r = 1.0;
                          color.g = 1.0;
                          color.b = 1.0;
                      }
                  }
                  
                  // Add XMP logo under the text
                  float logoScale = 0.05;
                  float2 logoP = float2((uv.x - 0.5) * aspect / logoScale, (uv.y - 0.65) / logoScale);
                  if (DrawXMPLogo(logoP)) color.rgb = float3(1.0, 1.0, 1.0);
              } else {
                  // Style 1: VCR
                  // Noise:
                  float noise = frac(sin(dot(uv * Time, float2(12.9898, 78.233))) * 43758.5453);
                  color.rgb += noise * 0.05;
                  
                  // Scanlines
                  color.rgb -= sin(uv.y * 800.0) * 0.04;
                  
                  // State Text
                  float2 p = float2(uv.x * aspect - 0.2, uv.y - 0.1);
                  bool playDraw = false;
                  
                  if (PlaybackState == 1.0) { // PLAY
                      if (DrawLetter(p, 0)) playDraw = true;
                      if (DrawLetter(p - float2(0.12, 0.0), 1)) playDraw = true;
                      if (DrawLetter(p - float2(0.24, 0.0), 2)) playDraw = true;
                      if (DrawLetter(p - float2(0.36, 0.0), 3)) playDraw = true;
                  } else if (PlaybackState == 2.0) { // PAUSE
                      if (DrawLetter(p, 0)) playDraw = true;
                      if (DrawLetter(p - float2(0.12, 0.0), 2)) playDraw = true;
                      if (DrawLetter(p - float2(0.24, 0.0), 4)) playDraw = true;
                      if (DrawLetter(p - float2(0.36, 0.0), 5)) playDraw = true;
                      if (DrawLetter(p - float2(0.48, 0.0), 6)) playDraw = true;
                  } else { // STOP
                      if (DrawLetter(p, 5)) playDraw = true;
                      if (DrawLetter(p - float2(0.12, 0.0), 7)) playDraw = true;
                      if (DrawLetter(p - float2(0.24, 0.0), 8)) playDraw = true;
                      if (DrawLetter(p - float2(0.36, 0.0), 0)) playDraw = true;
                  }
                  
                  bool logoDraw = false;
                  
                  // Add XMP logo in bottom right
                  float logoScale = 0.08;
                  float2 logoP = float2((uv.x - 1.0) * aspect / logoScale + 1.8, (uv.y - 1.0) / logoScale + 1.2);
                  if (DrawXMPLogo(logoP)) logoDraw = true;

                  if (playDraw || logoDraw) {
                      color.rgb = float3(1.0, 1.0, 1.0); // White text
                      // Add chromatic aberration for VHS look
                      if (frac(uv.y * 300.0 + Time * 5.0) < 0.5) {
                          color.r += 0.4;
                          color.b += 0.4;
                      }
                  }
              }
          }

          if (IsProjectorMode > 0.5 && HasBackBuffer > 0.5) {
              float3 sceneColor = BackBufferTexture.Sample(VideoSampler, screenUV).rgb;
              color.rgb = sceneColor + color.rgb * Opacity;
              color.a = 1.0;
          } else {
              color.a = clamp(Opacity, 0.0, 1.0);
          }
      }

      // Blend title / loading texture flush onto the TV frame.
      if (HasTitleTexture > 0.5 && HoverUV.x >= 0.0 && HoverUV.y >= 0.0) {
          float4 titleColor = TitleTexture.Sample(VideoSampler, uv);

          // Animated loading bar (matches 480x270 loading overlay layout; no per-frame texture uploads).
          if (IsLoadingOverlay > 0.5) {
              float barLeft = 0.302;
              float barRight = 0.698;
              float barTop = 0.535;
              float barBottom = 0.546;
              if (uv.x >= barLeft && uv.x <= barRight && uv.y >= barTop && uv.y <= barBottom) {
                  float barW = barRight - barLeft;
                  float segW = barW * 0.35;
                  float travel = barW - segW;
                  float segLeft = barLeft + travel * LoadingPulse;
                  if (uv.x >= segLeft && uv.x <= segLeft + segW) {
                      titleColor.rgb = float3(0.31, 0.76, 0.97);
                      titleColor.a = max(titleColor.a, 0.95);
                  }
              }
          }

          color.rgb = lerp(color.rgb, titleColor.rgb, titleColor.a);
      }
      
      // Apply soft occlusion mask
      color.a *= (1.0 - occlusion);
  } else if (isInside && occlusion >= 0.999) {
      // TV is fully hidden by geometry; show the scene, never bloom in front of the occluder.
      if (HasBackBuffer > 0.5) {
          color = float4(BackBufferTexture.Sample(VideoSampler, screenUV).rgb, 1.0);
      }
  } else if (!isInside) {
      // Ambient glow on surrounding walls/geometry only, not on the TV surface or through occluders.
      float depthMask = 1.0;
      if (gameDepth < 0.0001) depthMask = 0; // Ignore skybox
      
      // Convert gameDepth (reversed Z) to viewZ
      float gameViewZ = (NearPlane * FarPlane) / (gameDepth * (FarPlane - NearPlane) + NearPlane);
      
      // Reconstruct the 3D world position of the game pixel
      float3 gameWorldPos = CameraPos + rayDir * (gameViewZ / dot(rayDir, -CameraForward));
      
      // Skip pixels in front of the TV plane (e.g. the player occluding the screen).
      if (t > 0.0) {
          float tvViewZ = dot(rayDir * t, -CameraForward);
          if (gameViewZ < tvViewZ - 0.05) {
              depthMask = 0.0;
          }
      }
      
      // Calculate TV dimensions in 3D world space
      float3 tvCenter3D = (CornerTR3D + CornerBL3D) * 0.5;
      float tvWidth3D = length(CornerTR3D - CornerTL3D);
      float tvHeight3D = length(CornerBL3D - CornerTL3D);
      float tvSize3D = max(tvWidth3D, tvHeight3D);
      
      // Calculate distance from the center of the TV in 3D world space
      float3 toPixel = gameWorldPos - tvCenter3D;
      float dist3D = length(toPixel);
      
      // Subtract half the TV size so the glow starts fading from the edges, not the center
      dist3D = max(0.0, dist3D - tvSize3D * 0.5);
      
      // Physical light dissipation based on world space distance
      float maxGlowRadius3D = max(4.0, tvSize3D * 1.5);
      float distanceFade = saturate(1.0 - (dist3D / maxGlowRadius3D)); 
      depthMask *= pow(distanceFade, 2.5); // Non-linear falloff for realism
      
      // Backlight directionality fade: shine behind the TV, not on objects in front
      float3 dirToPixel = dist3D > 0.001 ? normalize(toPixel) : float3(0, 0, 0);
      float dotNorm = dot(dirToPixel, tvNormal);
      float directionFade = smoothstep(0.5, -0.2, dotNorm);
      depthMask *= directionFade;
      
      if (depthMask > 0.001) {
          // Use a 144-point (12x12 grid) average texture sample to stabilize the glow color
          // and mitigate potential flicker from on-screen movement.
          float3 prominentColor = float3(0, 0, 0);
          for (float x = 0.05; x < 1.0; x += 0.0833) {
              for (float y = 0.05; y < 1.0; y += 0.0833) {
                  prominentColor += VideoTexture.Sample(VideoSampler, float2(x, y)).rgb;
              }
          }
          prominentColor /= 144.0;
          
          // Scale glow alpha by the computed video luminance.
          float luminance = dot(prominentColor, float3(0.299, 0.587, 0.114));
          float alpha = saturate(depthMask * luminance * 3.5); 
          
          // Clamp intensity ceiling to prevent extreme highlights overexposure.
          alpha = clamp(alpha, 0.0, 0.45) * Opacity; 
          
          if (EnableTvGlow < 0.5) alpha = 0.0;
          
          // Compute additive backlight intensity.
          float3 light = prominentColor * alpha;
          
          if (HasBackBuffer > 0.5) {
              float3 sceneColor = BackBufferTexture.Sample(VideoSampler, screenUV).rgb;
              float bbAlpha = BackBufferTexture.Sample(DepthSampler, screenUV).a;
              
              // Color Dodge Blend: Final = Scene / (1.0 - Light) to preserve dark shadows
              // while illuminating brighter midtones.
              float3 glowedScene = saturate(sceneColor / max(0.001, 1.0 - light));
              
              // Protect overlapping UI elements from overexposure by blending back original scene color.
              float3 finalColor = lerp(glowedScene, sceneColor, bbAlpha);
              
              color = float4(finalColor, 1.0); // Output pre-blended opaque color
          } else {
              // Fallback to standard transparent fog overlay if backbuffer is missing
              color = float4(prominentColor, alpha);
          }
      }
  }

  // The UI compositing block was moved to the bottom of the shader to draw OVER the media controls.
  
  // Media Controls UI overlay
  if (isInside && occlusion < 0.999 && HoverUV.x >= 0.0 && HoverUV.y >= 0.0 && IsLockedTV >= 0.0) {
    // History Icon Top Left (0.02 - 0.08, 0.04 - 0.12)
    if (uv.x > 0.02 && uv.x < 0.08 && uv.y > 0.04 && uv.y < 0.12) {
       color.rgb = lerp(color.rgb, float3(0.05, 0.05, 0.05), 0.7);
       float px = (uv.x - 0.02) / 0.06;
       float py = (uv.y - 0.04) / 0.08;
       
       // draw clock
       float dist = distance(float2(px, py), float2(0.5, 0.5));
       if (abs(dist - 0.3) < 0.05) color.rgb = float3(1, 1, 1);
       if (px > 0.45 && px < 0.55 && py > 0.25 && py < 0.55) color.rgb = float3(1, 1, 1); // hour hand
       if (px > 0.45 && px < 0.7 && py > 0.45 && py < 0.55) color.rgb = float3(1, 1, 1); // minute hand
    }

    // DMCA Button (Top Right: 0.92 - 0.98, 0.04 - 0.12)
    if (uv.x > 0.92 && uv.x < 0.98 && uv.y > 0.04 && uv.y < 0.12) {
       color.rgb = lerp(color.rgb, float3(0.05, 0.05, 0.05), 0.7);
       float px = (uv.x - 0.92) / 0.06;
       float py = (uv.y - 0.04) / 0.08;
       
       // draw 'i' for info/report
       if (px > 0.4 && px < 0.6) {
           if (py > 0.2 && py < 0.3) color.rgb = float3(1, 1, 1);
           if (py > 0.4 && py < 0.8) color.rgb = float3(1, 1, 1);
       }
    }

    if (uv.y > 0.85) {
      // Background
      color.rgb = lerp(color.rgb, float3(0.05, 0.05, 0.05), 0.7);
      
      // Prev (0.02 - 0.06)
      if (uv.x > 0.02 && uv.x < 0.06 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.02) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         if (px < 0.2) color.rgb = float3(1, 1, 1);
         else if (px > 0.3 && px > abs(py - 0.5) * 2.0) color.rgb = float3(1, 1, 1);
      }
      
      // Rewind (0.07 - 0.11)
      if (uv.x > 0.07 && uv.x < 0.11 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.07) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         float px1 = frac(px * 2.0);
         if (px1 > abs(py - 0.5) * 2.0) color.rgb = float3(1, 1, 1);
      }

      // Play/Pause (0.12 - 0.16)
      if (uv.x > 0.12 && uv.x < 0.16 && uv.y > 0.88 && uv.y < 0.94) {
         if (PlaybackState == 1.0) {
            float px = (uv.x - 0.12) / 0.04;
            if ((px > 0.2 && px < 0.4) || (px > 0.6 && px < 0.8)) color.rgb = float3(1, 1, 1);
         } else {
            float px = (uv.x - 0.12) / 0.04;
            float py = (uv.y - 0.88) / 0.06;
            if (px < 1.0 - abs(py - 0.5) * 2.0) color.rgb = float3(1, 1, 1);
         }
      }

      // Fast Forward (0.17 - 0.21)
      if (uv.x > 0.17 && uv.x < 0.21 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.17) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         float px1 = frac(px * 2.0);
         if (px1 < 1.0 - abs(py - 0.5) * 2.0) color.rgb = float3(1, 1, 1);
      }
      
      // Next (0.22 - 0.26)
      if (uv.x > 0.22 && uv.x < 0.26 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.22) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         if (px > 0.8) color.rgb = float3(1, 1, 1);
         else if (px < 0.7 && px < 1.0 - abs(py - 0.5) * 2.0) color.rgb = float3(1, 1, 1);
      }
      
      // Stop (0.27 - 0.31)
      if (uv.x > 0.27 && uv.x < 0.31 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.27) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         if (px > 0.1 && px < 0.9 && py > 0.1 && py < 0.9) color.rgb = float3(1, 1, 1);
      }
      
      // Seek Bar & Volume Track
      if (uv.y > 0.90 && uv.y < 0.92 && uv.x > 0.32 && uv.x < 0.60) {
         float barProgress = (uv.x - 0.32) / 0.28;
         if (barProgress < Progress) color.rgb = float3(0.8, 0.2, 0.2);
         else if (barProgress < BufferProgress) color.rgb = float3(0.55, 0.42, 0.18);
         else color.rgb = float3(0.3, 0.3, 0.3);
      }
      if (uv.y > 0.95 && uv.y < 0.97 && uv.x > 0.32 && uv.x < 0.60) {
         float volProgress = (uv.x - 0.32) / 0.28;
         if (volProgress < Volume / 3.0) color.rgb = float3(0.2, 0.6, 0.8);
         else color.rgb = float3(0.3, 0.3, 0.3);
      }
      
      // Loop (0.62 - 0.66)
      if (uv.x > 0.62 && uv.x < 0.66 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.62) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         bool draw = false;
         if (px < 0.4 && py < 0.6 && abs(distance(float2(px, py), float2(0.4, 0.5)) - 0.25) < 0.05) draw = true;
         if (px >= 0.4 && px <= 0.6 && abs(py - 0.25) < 0.05) draw = true;
         if (px >= 0.6 && px <= 0.8 && py >= 0.1 && py <= 0.4) {
             float lx = (px - 0.6) * 5.0;
             float ly = (py - 0.1) * 3.333;
             if (lx < 1.0 - abs(ly - 0.5) * 2.0) draw = true;
         }
         if (px > 0.6 && py > 0.4 && abs(distance(float2(px, py), float2(0.6, 0.5)) - 0.25) < 0.05) draw = true;
         if (px >= 0.4 && px <= 0.6 && abs(py - 0.75) < 0.05) draw = true;
         if (px >= 0.2 && px <= 0.4 && py >= 0.6 && py <= 0.9) {
             float lx = (px - 0.2) * 5.0;
             float ly = (py - 0.6) * 3.333;
             if (lx > abs(ly - 0.5) * 2.0) draw = true;
         }
         if (draw) {
             if (IsLooping > 0.5) color.rgb = float3(0.2, 0.8, 0.3);
             else color.rgb = float3(1, 1, 1);
         }
      }

      // Shuffle (0.68 - 0.72)
      if (uv.x > 0.68 && uv.x < 0.72 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.68) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         bool draw = false;
         float sy1 = lerp(0.25, 0.75, smoothstep(0.35, 0.65, px));
         float sy2 = lerp(0.75, 0.25, smoothstep(0.35, 0.65, px));
         if (px > 0.15 && px < 0.65 && abs(py - sy1) < 0.05) draw = true;
         if (px > 0.15 && px < 0.65 && abs(py - sy2) < 0.05 && abs(px - 0.5) > 0.06) draw = true;
         if (px >= 0.65 && px <= 0.85 && py >= 0.6 && py <= 0.9) {
             float lx = (px - 0.65) * 5.0;
             float ly = (py - 0.6) * 3.333;
             if (lx < 1.0 - abs(ly - 0.5) * 2.0) draw = true;
         }
         if (px >= 0.65 && px <= 0.85 && py >= 0.1 && py <= 0.4) {
             float lx = (px - 0.65) * 5.0;
             float ly = (py - 0.1) * 3.333;
             if (lx < 1.0 - abs(ly - 0.5) * 2.0) draw = true;
         }
         if (draw) {
             if (IsShuffle > 0.5) color.rgb = float3(0.2, 0.8, 0.3);
             else color.rgb = float3(1, 1, 1);
         }
      }

      // Refresh (0.74 - 0.78)
      if (uv.x > 0.74 && uv.x < 0.78 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.74) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         bool draw = false;
         float dist = distance(float2(px, py), float2(0.5, 0.5));
         if (abs(dist - 0.25) < 0.05) {
             if (!(px > 0.5 && py < 0.4)) draw = true;
         }
         if (px >= 0.4 && px <= 0.6 && py >= 0.1 && py <= 0.4) {
             float lx = (px - 0.4) * 5.0;
             float ly = (py - 0.1) * 3.333;
             if (lx < 1.0 - abs(ly - 0.5) * 2.0) draw = true;
         }
         if (draw) color.rgb = float3(1, 1, 1);
      }
      
      // Lock Icon (0.80 - 0.84)
      if (IsLockedTV >= 0.0 && uv.x > 0.80 && uv.x < 0.84 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.80) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         if (px > 0.2 && px < 0.8 && py > 0.4 && py < 0.9) {
             if (IsLockedTV > 0.5) color.rgb = float3(0.9, 0.7, 0.2);
             else color.rgb = float3(0.6, 0.6, 0.6);
             if (px > 0.45 && px < 0.55 && py > 0.6 && py < 0.8) color.rgb = float3(0.1, 0.1, 0.1);
         }
         if (py > 0.1 && py <= 0.4) {
             if (IsLockedTV < 0.5 && px > 0.5) { }
             else if (px > 0.3 && px < 0.7 && py < 0.2) color.rgb = float3(0.8, 0.8, 0.8);
             else if ((px > 0.3 && px < 0.4) || (px > 0.6 && px < 0.7)) color.rgb = float3(0.8, 0.8, 0.8);
         }
      }
      
      // Paste Icon (0.85 - 0.89)
      if (uv.x > 0.85 && uv.x < 0.89 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.85) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         if (px > 0.2 && px < 0.8 && py > 0.1 && py < 0.9) {
             if (py < 0.3 && px > 0.4 && px < 0.6) color.rgb = float3(0.9, 0.9, 0.9);
             else if (py > 0.3) {
                 if ((py > 0.45 && py < 0.55) || (py > 0.65 && py < 0.75)) color.rgb = float3(0.5, 0.5, 0.5);
                 else color.rgb = float3(0.8, 0.8, 0.8);
             } else color.rgb = float3(0.4, 0.3, 0.2);
         }
      }
      
      // Queue Icon (0.90 - 0.94)
      if (uv.x > 0.90 && uv.x < 0.94 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.90) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         if ((px > 0.4 && px < 0.6 && py > 0.2 && py < 0.8) || (py > 0.4 && py < 0.6 && px > 0.2 && px < 0.8)) color.rgb = float3(0.2, 0.8, 0.3);
      }
      
      // Kill Icon (0.95 - 0.99)
      if (uv.x > 0.95 && uv.x < 0.99 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.95) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         // Draw X
         float d1 = abs(px - py);
         float d2 = abs(px - (1.0 - py));
         if (d1 < 0.15 || d2 < 0.15) {
             color.rgb = float3(0.9, 0.2, 0.2);
         }
      }
    }
  }
  
  // Check if this pixel is inside any UI bounding box
  bool insideUI = false;
  int rectType = 0; // 0 = Standard, 1 = MJI, 2 = ActionDetail
  for (int i = 0; i < UIRectCount; i++) {
      float4 r = UIRects[i];
      if (pixelPos.x >= r.x && pixelPos.x <= r.x + r.z &&
          pixelPos.y >= r.y && pixelPos.y <= r.y + r.w) {
          insideUI = true;
          
          int vecIdx = i / 4;
          int compIdx = i % 4;
          float typeVal = UIRectTypes[vecIdx][compIdx];
          
          if (typeVal > 3.5) {
              rectType = 4; // _ToDoList takes highest precedence
          } else if (typeVal > 2.5 && rectType < 4) {
              rectType = 3; // MjlHud
          } else if (typeVal > 1.5 && rectType < 3) {
              rectType = 2; // ActionDetail
          } else if (typeVal > 0.5 && rectType < 2) {
              rectType = 1; // MJI
          }
      }
  }
  
  // Fallback: If AddonRects failed to capture (e.g. FFXIV update broke RaptureAtkUnitManager),
  // allow the alpha mask to still apply everywhere.
  if (UIRectCount == 0) {
      insideUI = true;
  }
  
  if (insideUI && isInside && occlusion < 0.999) {
      float4 bbColor = BackBufferTexture.Sample(VideoSampler, screenUV);
      
      if (HasPreUI > 0.5) {
          // Mathematic UI blending, subtract the pre-UI scene (Unk68)
          // from the post-UI scene (BackBuffer) to get the exact UI contribution.
          float4 preUiColor = PreUITexture.Sample(VideoSampler, screenUV);
          float bbAlpha = BackBufferTexture.Sample(DepthSampler, screenUV).a;
          
          float3 uiDiff = abs(bbColor.rgb - preUiColor.rgb);
          float diffMax = max(max(uiDiff.r, uiDiff.g), uiDiff.b);
          float gameDepth = DepthTexture.Sample(DepthSampler, screenUV).r;
          
          // The emissive skybox incorrectly writes bbAlpha=1.0.
          // We can detect it because it's at max depth (gameDepth=0) and has no UI difference.
          // If it's the skybox without UI over it, we force trueAlpha to 0.
          float trueAlpha = bbAlpha;
          if (gameDepth < 0.00001 && diffMax < 0.02) {
              trueAlpha = 0.0;
          }
          
          if (trueAlpha > 0.01) {
              // Get extrapolated subtractive vignette to match FFXIV's post-processing
              float3 vignetteExtrapolated = VignetteExtrapolatedTexture.Sample(VideoSampler, screenUV).rgb;
              float3 trueBackground = saturate(preUiColor.rgb - vignetteExtrapolated);
              
              float diffMax2 = max(max(abs(bbColor.r - trueBackground.r), abs(bbColor.g - trueBackground.g)), abs(bbColor.b - trueBackground.b));
                
              // Estimate the true UI alpha cleanly using the color difference.
              // We use a clean scaler (1.5x) rather than reverse-engineering the division formula.
              
              float estimatedAlpha = saturate(diffMax2 * 1.5);
              
              float nativeAlpha = bbColor.a;
              float unk68Alpha = PreUITexture.Sample(VideoSampler, screenUV).a;
              float alphaDiff = abs(nativeAlpha - unk68Alpha);
              
              bool isSkybox = (gameDepth < 0.00001);
              
              if (isSkybox) {
                  if (UseDifferenceFallback > 0.5) {
                      // Wanderer's Campfire spawned, use difference fallback (estimatedAlpha)
                      // This fixes the indoor emissive skybox blocking the TV.
                      trueAlpha = (diffMax2 > 0.02) ? estimatedAlpha : 0.0;
                  } else {
                      // No campfire, use SwapChainBackBuffer alpha natively.
                      // The fog has low alpha so it doesn't block the TV, and UI occludes the fog.
                      trueAlpha = nativeAlpha;
                  }
              } else {
                  // Over geometry, diffMax2 can have holes if UI color == Geometry color.
                  // But alphaDiff works here to fill the holes.
                  trueAlpha = saturate(max(estimatedAlpha, alphaDiff));
              }
              
              // Mathematic UI reconstruction.
              // For opaque UI (trueAlpha=1), this exactly outputs bbColor (the originating UI pixel).
              // For translucent UI (e.g. drop shadows), it mathematically removes the FFXIV background
              // (preUiColor + vignette) and replaces it with the TV pixel, preserving the exact shadow.
              color.rgb = saturate(bbColor.rgb + (color.rgb - trueBackground) * (1.0 - trueAlpha));
          }
      } else {
          // Fallback to old alpha masking if Unk68 is somehow missing
          float bbAlpha = BackBufferTexture.Sample(DepthSampler, screenUV).a;
      
      if (color.a > 0.5) {
          if (rectType == 4) {
              // _ToDoList: threshold 90, backdrop #453C26
              float threshold = 90.0 / 255.0;
              float bbLuminance = dot(bbColor.rgb, float3(0.299, 0.587, 0.114));
              float colorBoost = smoothstep(0.3, 0.6, bbLuminance);
              float isPureWhite = max(smoothstep(threshold - 0.02, 1.0, bbAlpha), colorBoost);
              float3 shadowColor = float3(69.0 / 255.0, 60.0 / 255.0, 38.0 / 255.0); // #453C26
              float3 targetColor = lerp(shadowColor, bbColor.rgb, isPureWhite);
              color.rgb = color.rgb * saturate(1.0 - bbAlpha) + targetColor * bbAlpha;
          } else if (rectType == 3) {
              // MjlHud: threshold 233, backdrop #ACA393
              float threshold = 233.0 / 255.0;
              float isPureWhite = smoothstep(threshold - 0.02, 1.0, bbAlpha);
              float3 shadowColor = float3(172.0 / 255.0, 163.0 / 255.0, 147.0 / 255.0); // #ACA393
              float3 targetColor = lerp(shadowColor, bbColor.rgb, isPureWhite);
              color.rgb = color.rgb * saturate(1.0 - bbAlpha) + targetColor * bbAlpha;
          } else if (rectType == 2) {
              // ActionDetail: ultra aggressive threshold, only 255 cuts the TV
              float threshold = 254.0 / 255.0;
              float isPureWhite = smoothstep(threshold - 0.02, 1.0, bbAlpha);
              float3 shadowColor = float3(48.0 / 255.0, 34.0 / 255.0, 21.0 / 255.0); // #302215
              float3 targetColor = lerp(shadowColor, bbColor.rgb, isPureWhite);
              color.rgb = color.rgb * saturate(1.0 - bbAlpha) + targetColor * bbAlpha;
          } else if (rectType == 1) {
              // MJI: threshold 152
              float threshold = 152.0 / 255.0;
              float isPureWhite = smoothstep(threshold - 0.02, threshold, bbAlpha);
              
              float3 blendedBlack = color.rgb * saturate(1.0 - bbAlpha);
              color.rgb = blendedBlack + (bbColor.rgb * bbAlpha * isPureWhite);
          } else {
              // For all other UI (standard game UI), use the pure standard alpha blend
              if (UIBlendThreshold > 0.5) {
                  float threshold = UIBlendThreshold;
                  float isPureWhite = smoothstep(threshold - 0.02, 1.0, bbAlpha);
                  float3 shadowColor = float3(0.0, 0.0, 0.0);
                  float3 targetColor = lerp(shadowColor, bbColor.rgb, isPureWhite);
                  color.rgb = color.rgb * saturate(1.0 - bbAlpha) + targetColor * bbAlpha;
              } else {
                  color.rgb = color.rgb * saturate(1.0 - bbAlpha) + bbColor.rgb * bbAlpha;
              }
          }
      }
    }
  }
  
  return color;
}
";

    public bool IsInitialized => _initialized;
    public string InitError => _initError;
    public ID3D11ShaderResourceView OutputSRV => _renderTargetSRV;

    public bool Initialize() {
      if (_initialized || _disposed) return _initialized;

      try {
        var ffxivDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null) {
          _initError = "FFXIV D3D11 device context not available.";
          return false;
        }

        var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
        System.Runtime.InteropServices.Marshal.AddRef(contextPtr);
        _context = new ID3D11DeviceContext(contextPtr);
        _device = _context.Device;

        // Compile shaders
        var vsBytecode = Compiler.Compile(ShaderCode, "VS", "", "vs_5_0");
        _vertexShader = _device.CreateVertexShader(vsBytecode.Span);

        var psBytecode = Compiler.Compile(ShaderCode, "PS", "", "ps_5_0");
        _pixelShader = _device.CreatePixelShader(psBytecode.Span);

        // Constant buffers (D3D11 requires constant buffer size be a multiple of 16 bytes)
        int constantBufferSize = (Marshal.SizeOf<PSConstants>() + 15) & ~15;
        _constantBuffer = _device.CreateBuffer(new BufferDescription {
          ByteWidth = constantBufferSize,
          Usage = ResourceUsage.Default,
          BindFlags = BindFlags.ConstantBuffer,
          CPUAccessFlags = CpuAccessFlags.None,
        });

        _uiRectBuffer = _device.CreateBuffer(new BufferDescription {
          ByteWidth = Marshal.SizeOf<UIConstants>(),
          Usage = ResourceUsage.Default,
          BindFlags = BindFlags.ConstantBuffer,
          CPUAccessFlags = CpuAccessFlags.None,
        });

        // Blend state: write-through
        var blendDesc = new BlendDescription();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription {
          BlendEnable = false,
          RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendState = _device.CreateBlendState(blendDesc);

        // Samplers
        _videoSampler = _device.CreateSamplerState(new SamplerDescription {
          Filter = Filter.MinMagMipLinear,
          AddressU = TextureAddressMode.Clamp,
          AddressV = TextureAddressMode.Clamp,
          AddressW = TextureAddressMode.Clamp,
        });
        _depthSampler = _device.CreateSamplerState(new SamplerDescription {
          Filter = Filter.MinMagMipPoint,
          AddressU = TextureAddressMode.Clamp,
          AddressV = TextureAddressMode.Clamp,
          AddressW = TextureAddressMode.Clamp,
        });

        _initialized = true;
        return true;
      } catch (Exception ex) {
        _initError = $"DepthTestedRenderer init failed: {ex.Message}";
        return false;
      }
    }

    private void EnsureRenderTarget(int width, int height) {
      if (_renderTarget != null && _rtWidth == width && _rtHeight == height) return;

      _renderTargetView?.Dispose();
      _renderTargetSRV?.Dispose();
      _renderTarget?.Dispose();

      _rtWidth = width;
      _rtHeight = height;

      var texDesc = new Texture2DDescription {
        Width = width,
        Height = height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.R8G8B8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        CPUAccessFlags = CpuAccessFlags.None,
      };

      _renderTarget = _device.CreateTexture2D(texDesc);
      _renderTargetView = _device.CreateRenderTargetView(_renderTarget);
      _renderTargetSRV = _device.CreateShaderResourceView(_renderTarget);
    }

    public unsafe bool Render(
      (Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl) screenCorners,
      (Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl) worldCorners,
      Vector3 cameraPos,
      Vector3 cameraForward, Vector3 cameraRight, Vector3 cameraUp, float fovY, float aspectRatio,
      IntPtr videoSrvPtr,
      ID3D11ShaderResourceView depthSrv,
      Vector4 cornerDepths,
      float nearPlane, float farPlane,
      int screenWidth, int screenHeight,
      ID3D11ShaderResourceView uiLayerSrv,
      Vector2? hoverUV, float progress, float bufferProgress, float playbackState, float lockState,
      float minDepth, float maxDepth, float volume,
      float renderWidth, float renderHeight,
      List<(int X, int Y, int W, int H, string Name)> uiRects, IntPtr titleSrvPtr = default,
      bool isLooping = false, bool isShuffle = false, float time = 0, float showScreensaver = 0,
      float videoAspectRatio = 0, IntPtr gbuffer2SrvPtr = default, IntPtr gbuffer3SrvPtr = default, IntPtr transparentUiSrvPtr = default, IntPtr vignetteExtrapolatedSrvPtr = default, bool useDifferenceFallback = false, float opacity = 1.0f, bool isProjectorMode = false, Vector3? screensaverColor = null, int screensaverStyle = 0, float uiBlendThreshold = 0.0f, float uvBottom = 1.0f, float uvRight = 1.0f, bool enableTvGlow = true, float loadingPulse = 0f, bool isLoadingOverlay = false, IntPtr idleBrandingSrvPtr = default, float idleBrandingAspect = 1.0f) {

      if (!_initialized || _disposed || videoSrvPtr == IntPtr.Zero || depthSrv == null) return false;

      EnsureRenderTarget(screenWidth, screenHeight);

      var savedRTVs = new ID3D11RenderTargetView[1];
      ID3D11DepthStencilView savedDSV;
      _context.OMGetRenderTargets(1, savedRTVs, out savedDSV);

      try {
        _context.ClearRenderTargetView(_renderTargetView, new Vortice.Mathematics.Color4(0, 0, 0, 0));

        // Update constants
        var constants = new PSConstants {
          CornerTL = screenCorners.tl,
          CornerTR = screenCorners.tr,
          CornerBR = screenCorners.br,
          CornerBL = screenCorners.bl,
          ScreenSize = new Vector2(screenWidth, screenHeight),
          NearPlane = nearPlane,
          FarPlane = farPlane,
          HoverUV = hoverUV ?? new Vector2(-1, -1),
          CornerDepths = cornerDepths,

          CameraPos = cameraPos,
          FovY = fovY,
          CameraForward = cameraForward,
          AspectRatio = aspectRatio,
          CameraRight = cameraRight,
          CameraUp = cameraUp,

          CornerTL3D = worldCorners.tl,
          CornerTR3D = worldCorners.tr,
          CornerBL3D = worldCorners.bl,
          VideoAspectRatio = videoAspectRatio,

          Progress = progress,
          BufferProgress = bufferProgress,
          PlaybackState = playbackState,
          DynamicMinDepth = minDepth,
          DynamicMaxDepth = maxDepth,
          HasBackBuffer = uiLayerSrv != null ? 1.0f : 0.0f,
          IsLockedTV = lockState,
          Volume = volume,
          RenderResolution = new Vector2(renderWidth, renderHeight),
          HasTitleTexture = titleSrvPtr != IntPtr.Zero ? 1.0f : 0.0f,
          IsLooping = isLooping ? 1.0f : 0.0f,
          IsShuffle = isShuffle ? 1.0f : 0.0f,
          Time = time,
          ShowScreensaver = showScreensaver,
          HasPreUI = transparentUiSrvPtr != IntPtr.Zero ? 1.0f : 0.0f,
          UseDifferenceFallback = useDifferenceFallback ? 1.0f : 0.0f,
          Opacity = opacity,
          IsProjectorMode = isProjectorMode ? 1.0f : 0.0f,
          ScreensaverColor = screensaverColor ?? new Vector3(0.0f, 0.0f, 0.0f),
          ScreensaverStyle = screensaverStyle,
          UIBlendThreshold = uiBlendThreshold,
          UVBottomEdge = uvBottom,
          UVRightEdge = uvRight,
          EnableTvGlow = enableTvGlow ? 1.0f : 0.0f,
          LoadingPulse = loadingPulse,
          IsLoadingOverlay = isLoadingOverlay ? 1.0f : 0.0f,
          HasIdleBranding = idleBrandingSrvPtr != IntPtr.Zero ? 1.0f : 0.0f,
          IdleBrandingAspect = idleBrandingAspect,
        };
          _context.UpdateSubresource(constants, _constantBuffer);

        var uiConsts = new UIConstants {
            UIRectCount = Math.Min(64, uiRects?.Count ?? 0)
        };
        for (int i = 0; i < uiConsts.UIRectCount; i++) {
            var r = uiRects[i];
            uiConsts.UIRects[i * 4 + 0] = r.X;
            uiConsts.UIRects[i * 4 + 1] = r.Y;
            uiConsts.UIRects[i * 4 + 2] = r.W;
            uiConsts.UIRects[i * 4 + 3] = r.H;
            
            // Flag addons so the shader treats them differently: 2 = _ActionContents, 3 = MJI (Island Sanctuary HUD), 4 = _ToDoList, 0 = Standard
            if (r.Name != null && r.Name.StartsWith("_ActionContents")) {
                uiConsts.UIRectTypes[i] = 2.0f;
            } else if (r.Name != null && r.Name.StartsWith("MJI")) {
                uiConsts.UIRectTypes[i] = 3.0f;
            } else if (r.Name != null && r.Name.StartsWith("_ToDoList")) {
                uiConsts.UIRectTypes[i] = 4.0f;
            } else {
                uiConsts.UIRectTypes[i] = 0.0f;
            }
        }
        _context.UpdateSubresource(uiConsts, _uiRectBuffer);

        // Set pipeline
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);

        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);

        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetConstantBuffer(1, _uiRectBuffer);
        var srvs = new ID3D11ShaderResourceView[9];
        if (videoSrvPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.AddRef(videoSrvPtr);
        srvs[0] = videoSrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(videoSrvPtr) : null;
        srvs[1] = depthSrv;
        srvs[2] = uiLayerSrv;
        if (titleSrvPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.AddRef(titleSrvPtr);
        srvs[3] = titleSrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(titleSrvPtr) : null;
        if (transparentUiSrvPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.AddRef(transparentUiSrvPtr);
        srvs[4] = transparentUiSrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(transparentUiSrvPtr) : null;
        if (gbuffer2SrvPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.AddRef(gbuffer2SrvPtr);
        srvs[5] = gbuffer2SrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(gbuffer2SrvPtr) : null;
        if (gbuffer3SrvPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.AddRef(gbuffer3SrvPtr);
        srvs[6] = gbuffer3SrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(gbuffer3SrvPtr) : null;
        if (vignetteExtrapolatedSrvPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.AddRef(vignetteExtrapolatedSrvPtr);
        srvs[7] = vignetteExtrapolatedSrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(vignetteExtrapolatedSrvPtr) : null;
        if (idleBrandingSrvPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.AddRef(idleBrandingSrvPtr);
        srvs[8] = idleBrandingSrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(idleBrandingSrvPtr) : null;
        
        _context.PSSetShaderResources(0, 9, srvs);
        _context.PSSetSampler(0, _videoSampler);
        _context.PSSetSampler(1, _depthSampler);

        _context.OMSetBlendState(_blendState);
        _context.RSSetViewport(0, 0, screenWidth, screenHeight);
        _context.OMSetRenderTargets(_renderTargetView);

        _context.Draw(3, 0);

        return true;
      } finally {
        _context.OMSetRenderTargets(savedRTVs, savedDSV);
        _context.PSSetShaderResource(0, (ID3D11ShaderResourceView)null);
        _context.PSSetShaderResource(1, (ID3D11ShaderResourceView)null);
        _context.PSSetShaderResource(2, (ID3D11ShaderResourceView)null);
        _context.PSSetShaderResource(3, (ID3D11ShaderResourceView)null);
        _context.PSSetShaderResource(4, (ID3D11ShaderResourceView)null);
        _context.PSSetShaderResource(5, (ID3D11ShaderResourceView)null);
        _context.PSSetShaderResource(6, (ID3D11ShaderResourceView)null);
        _context.PSSetShaderResource(7, (ID3D11ShaderResourceView)null);
        _context.PSSetShaderResource(8, (ID3D11ShaderResourceView)null);
        
        savedRTVs[0]?.Dispose();
        savedDSV?.Dispose();
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;

      _renderTargetView?.Dispose();
      _renderTargetSRV?.Dispose();
      _renderTarget?.Dispose();
      _depthSampler?.Dispose();
      _videoSampler?.Dispose();
      _blendState?.Dispose();
      _constantBuffer?.Dispose();
      _uiRectBuffer?.Dispose();
      _pixelShader?.Dispose();
      _vertexShader?.Dispose();
      _device?.Dispose();
      _context?.Dispose();
      _device = null;
      _context = null;
    }
  }
}









