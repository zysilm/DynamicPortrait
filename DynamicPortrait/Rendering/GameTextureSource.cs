// SPDX-License-Identifier: AGPL-3.0-or-later
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using Silk.NET.Direct3D11;

namespace DynamicPortrait.Rendering;

internal static unsafe class GameTextureSource
{
    public static bool TryBackbufferSize(out int width, out int height)
    {
        width = height = 0;
        var device = Device.Instance();
        var chain = device == null ? null : device->SwapChain;
        if (chain == null || chain->DXGISwapChain == null) return false;
        var dxgi = (Silk.NET.DXGI.IDXGISwapChain*)chain->DXGISwapChain;
        var iid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        ID3D11Texture2D* source = null;
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(dxgi->GetBuffer(0, &iid, (void**)&source));
        try
        {
            Texture2DDesc desc;
            source->GetDesc(&desc);
            width = (int)desc.Width;
            height = (int)desc.Height;
            return width > 0 && height > 0;
        }
        finally { source->Release(); }
    }

}
