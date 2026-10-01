// SPDX-License-Identifier: AGPL-3.0-or-later
using DynamicPortrait.Rendering;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System.Runtime.InteropServices;

internal static unsafe class SwapChainTest
{
    public static void Run(OffscreenGpu gpu)
    {
        // Hidden test window, never shown. This is a real DXGI swap chain on WARP,
        // not a game window and not evidence of game-engine integration.
        var window = CreateWindowExW(0, "STATIC", "Portrait offline swapchain", 0,
            0, 0, 320, 240, 0, 0, 0, 0);
        if (window == 0) throw new InvalidOperationException("Could not create hidden test window");
        IDXGIFactory* factory = null;
        IDXGISwapChain* chain = null;
        ID3D11Texture2D* buffer = null;
        ID3D11RenderTargetView* target = null;
        try
        {
            // Silk 2.21 marks its headless loader obsolete; no Silk window is used.
#pragma warning disable CS0618
            using var api = DXGI.GetApi();
#pragma warning restore CS0618
            var factoryId = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
            Marshal.ThrowExceptionForHR(api.CreateDXGIFactory(&factoryId, (void**)&factory));
            var description = new SwapChainDesc
            {
                BufferDesc = new ModeDesc { Width = 320, Height = 240, Format = Format.FormatR8G8B8A8Unorm },
                SampleDesc = new SampleDesc(1, 0), BufferUsage = 0x20,
                BufferCount = 1, OutputWindow = window, Windowed = true, SwapEffect = SwapEffect.Discard,
            };
            Marshal.ThrowExceptionForHR(factory->CreateSwapChain((IUnknown*)gpu.Device, &description, &chain));
            var textureId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            Marshal.ThrowExceptionForHR(chain->GetBuffer(0, &textureId, (void**)&buffer));
            Marshal.ThrowExceptionForHR(gpu.Device->CreateRenderTargetView((ID3D11Resource*)buffer, null, &target));
            var color = stackalloc float[] { 0.2f, 0.4f, 0.8f, 0 };
            gpu.Context->ClearRenderTargetView(target, color);
            using var capture = new PortraitTextures();
            SwapChainCapture.Capture(capture, chain, gpu.Context, 128, 1);
            var pixels = gpu.ReadView(capture.Read().Handle);
            Program.Check("Actual DXGI GetBuffer capture reads rendered backbuffer RGB", pixels[0] == 51 && pixels[1] == 102 && pixels[2] == 204 && pixels[3] == 255);
            Program.Check("Backbuffer capture uses requested crop size", capture.Read().Width == 128 && capture.Read().Height == 128);
            Program.Check("Backbuffer source has color despite zero alpha", capture.ProbeResult.Contains("alpha=0.000..0.000") && capture.ProbeResult.Contains("maxRGB=0.800"));
            target->Release(); target = null;
            buffer->Release(); buffer = null;
            Marshal.ThrowExceptionForHR(chain->ResizeBuffers(1, 200, 160, Format.FormatR8G8B8A8Unorm, 0));
            Program.Check("Capture releases backbuffer reference so DXGI resize succeeds", true);
            SwapChainCapture.Capture(capture, chain, gpu.Context, 256, 1);
            Program.Check("Backbuffer reacquired after resize", capture.Read().Width == 160 && capture.Read().Height == 160);
        }
        finally
        {
            if (target != null) target->Release();
            if (buffer != null) buffer->Release();
            if (chain != null) chain->Release();
            if (factory != null) factory->Release();
            DestroyWindow(window);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")]
    [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
