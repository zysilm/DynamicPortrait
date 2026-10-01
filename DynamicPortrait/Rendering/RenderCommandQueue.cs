// SPDX-License-Identifier: AGPL-3.0-or-later
// Marker protocol adapted from WesleyLuk90/ffxiv-vr, RenderPipelineInjector.cs.
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.Interop;
using System.Collections.Concurrent;

namespace DynamicPortrait.Rendering;

internal sealed unsafe class RenderCommandQueue
{
    public readonly record struct CaptureRequest(long Id, int Width, int Height);
    private readonly ConcurrentDictionary<nint, CaptureRequest> pending = new();

    public bool TryTake(nint command, out CaptureRequest request) => pending.TryRemove(command, out request);
    public bool Register(nint command, CaptureRequest request) => pending.TryAdd(command, request);
    public void Clear() => pending.Clear();

    // A missing lookup must still leave a valid command for the native handler.
    public static RenderCommandSetTarget CreateFallback(Texture* target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        var result = new RenderCommandSetTarget { RenderTargetCount = 1 };
        result.RenderTargets[0] = target;
        return result;
    }

    public bool EnqueueCapture(long id, int width, int height)
    {
        var device = Device.Instance();
        var target = device == null || device->SwapChain == null ? null : device->SwapChain->BackBuffer;
        if (target == null || target->MipRenderTargets == null
            || target->MipRenderTargets->D3D11RenderTargetViewOrDepthStencilView == null) return false;
        var locals = ThreadLocals.ThreadLocalInstance();
        var context = locals == null ? null : locals->GraphicsKernelContext;
        if (context == null) return false;
        var marker = (RenderCommandSetTarget*)context->AllocateCommand((ulong)sizeof(RenderCommandSetTarget));
        if (marker == null) return false;
        // A real scene-target bind, NOT a zero-target command. DoSetTargetCommand reads
        // target[0]->Height3 even for count=0 on this client (the 0.1.0.0 crash).
        // The engine owns this frame's target lifetime, as for its other queued binds.
        *marker = CreateFallback(target);
        if (!Register((nint)marker, new CaptureRequest(id, width, height))) return false;
        context->PushBackCommand(marker);
        return true;
    }
}
