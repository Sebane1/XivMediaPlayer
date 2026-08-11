// Safe probe. Only accesses documented FFXIVClientStructs fields.
using System;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Vortice.Direct3D11;

namespace XivMediaPlayer.Compositing {
  internal static unsafe class DepthBufferProbe {
    public static string ProbeAllDepthTextures() {
      var info = "";
      try {
        var dev = Device.Instance();
        if (dev == null) return "Device.Instance() is null";

        info += $"Device ptr: {(IntPtr)dev:X}\n";

        // SwapChain depth
        var sc = dev->SwapChain;
        if (sc != null) {
          info += $"SwapChain ptr: {(IntPtr)sc:X}\n";
          info += $"SwapChain: {sc->Width}x{sc->Height}\n";
          
          var scDepth = sc->DepthStencil;
          if (scDepth != null && scDepth->D3D11Texture2D != null) {
            var d3d = new ID3D11Texture2D((IntPtr)scDepth->D3D11Texture2D);
            var desc = d3d.Description;
            info += $"  SwapChain.DepthStencil: {desc.Width}x{desc.Height}, {desc.Format}, Bind={desc.BindFlags}\n";
          } else {
            info += "  SwapChain.DepthStencil: null or no D3D tex\n";
          }
        }

        // RenderTargetManager depth
        var rtm = RenderTargetManager.Instance();
        if (rtm == null) {
          info += "RenderTargetManager: null\n";
          return info;
        }
        
        info += $"RenderTargetManager ptr: {(IntPtr)rtm:X}\n";
        
        var rtmDepth = rtm->DepthStencil;
        if (rtmDepth != null) {
          info += $"  RTM.DepthStencil ptr: {(IntPtr)rtmDepth:X}\n";
          info += $"  RTM.DepthStencil.TextureFormat: {rtmDepth->TextureFormat}\n";
          
          if (rtmDepth->D3D11Texture2D != null) {
            var d3d = new ID3D11Texture2D((IntPtr)rtmDepth->D3D11Texture2D);
            var desc = d3d.Description;
            info += $"  RTM.DepthStencil D3D: {desc.Width}x{desc.Height}, {desc.Format}, Bind={desc.BindFlags}\n";
          } else {
            info += "  RTM.DepthStencil.D3D11Texture2D: null\n";
          }
        } else {
          info += "  RTM.DepthStencil: null\n";
        }

        // Check if RTM DepthStencil is the same as SwapChain DepthStencil
        if (sc != null && sc->DepthStencil != null && rtm->DepthStencil != null) {
          bool same = (IntPtr)sc->DepthStencil == (IntPtr)rtm->DepthStencil;
          info += $"  Same texture? {same}\n";
          if (!same && sc->DepthStencil->D3D11Texture2D != null && rtm->DepthStencil->D3D11Texture2D != null) {
            bool sameD3D = (IntPtr)sc->DepthStencil->D3D11Texture2D == (IntPtr)rtm->DepthStencil->D3D11Texture2D;
            info += $"  Same D3D texture? {sameD3D}\n";
          }
        }

      } catch (Exception ex) {
        info += $"Error: {ex.Message}";
      }
      return info;
    }
  }
}
