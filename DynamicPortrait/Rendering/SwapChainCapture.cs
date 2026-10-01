// SPDX-License-Identifier: AGPL-3.0-or-later
using DynamicPortrait.Camera;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System.Runtime.InteropServices;

namespace DynamicPortrait.Rendering;

internal static unsafe class SwapChainCapture
{
    // GetBuffer returns an owned COM reference. Do not retain it across resize.
    public static void Capture(PortraitTextures destination, IDXGISwapChain* swapChain,
        ID3D11DeviceContext* context, int resolution, float aspect)
    {
        var buffer = Acquire(swapChain);
        try
        {
            Texture2DDesc desc;
            buffer->GetDesc(&desc);
            var (width, height) = PortraitCamera.Crop((int)desc.Width, (int)desc.Height, resolution, aspect);
            destination.Capture(buffer, context, width, height);
        }
        finally { buffer->Release(); }
    }

    public static void CaptureRegion(PortraitTextures destination, IDXGISwapChain* swapChain,
        ID3D11DeviceContext* context, int width, int height, int expectedSourceWidth, int expectedSourceHeight)
    {
        var buffer = Acquire(swapChain);
        try
        {
            Texture2DDesc desc;
            buffer->GetDesc(&desc);
            if (desc.Width != expectedSourceWidth || desc.Height != expectedSourceHeight)
                throw new InvalidOperationException("Backbuffer resized during portrait rendering; restart rendering after resize.");
            destination.Capture(buffer, context, width, height);
        }
        finally { buffer->Release(); }
    }

    private static ID3D11Texture2D* Acquire(IDXGISwapChain* swapChain)
    {
        if (swapChain == null) throw new InvalidOperationException("DXGI swap chain unavailable");
        var iid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // ID3D11Texture2D
        ID3D11Texture2D* buffer = null;
        Marshal.ThrowExceptionForHR(swapChain->GetBuffer(0, &iid, (void**)&buffer));
        return buffer;
    }
}
