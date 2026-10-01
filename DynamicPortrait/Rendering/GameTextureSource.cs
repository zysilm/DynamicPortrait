// SPDX-License-Identifier: AGPL-3.0-or-later
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Silk.NET.Direct3D11;

namespace DynamicPortrait.Rendering;

internal static unsafe class GameTextureSource
{
    public static void Capture(PortraitTextures textures, int width, int height)
    {
        var manager = RenderTargetManager.Instance();
        var device = Device.Instance();
        if (manager == null || device == null || manager->ToneAdjustSource == null)
            throw new InvalidOperationException("Scene target unavailable");
        textures.Capture((ID3D11Texture2D*)manager->ToneAdjustSource->D3D11Texture2D,
            (ID3D11DeviceContext*)device->D3D11DeviceContext, width, height);
    }

    public static bool TrySourceSize(out int width, out int height)
    {
        width = height = 0;
        var manager = RenderTargetManager.Instance();
        if (manager == null || manager->ToneAdjustSource == null) return false;
        var source = (ID3D11Texture2D*)manager->ToneAdjustSource->D3D11Texture2D;
        if (source == null) return false;
        Texture2DDesc description;
        source->GetDesc(&description);
        width = (int)description.Width;
        height = (int)description.Height;
        return width > 0 && height > 0;
    }
}
