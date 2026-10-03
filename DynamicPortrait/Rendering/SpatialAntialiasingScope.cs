// SPDX-License-Identifier: AGPL-3.0-or-later
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace DynamicPortrait.Rendering;

// Temporary native render configuration only; never writes the game's saved
// configuration. Both views must use the same spatial path for this experiment.
internal readonly unsafe struct SpatialAntialiasingScope : IDisposable
{
    private readonly GraphicsConfig* graphics;
    private readonly uint antialiasing;
    private readonly byte upscale;

    public SpatialAntialiasingScope(GraphicsConfig* graphics, bool enabled)
    {
        this.graphics = enabled ? graphics : null;
        antialiasing = upscale = 0;
        if (this.graphics == null) return;
        antialiasing = graphics->AntiAliasing;
        upscale = graphics->GraphicsRezoUpscaleType;
        graphics->AntiAliasing = 1; // FXAA.
        graphics->GraphicsRezoUpscaleType = 0; // Non-temporal upscale path.
    }

    public void Dispose()
    {
        if (graphics == null) return;
        // Preserve a configuration change made by another callback during Tick.
        if (graphics->AntiAliasing == 1) graphics->AntiAliasing = antialiasing;
        if (graphics->GraphicsRezoUpscaleType == 0) graphics->GraphicsRezoUpscaleType = upscale;
    }
}
