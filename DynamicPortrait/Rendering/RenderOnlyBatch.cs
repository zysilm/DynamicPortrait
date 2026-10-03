// SPDX-License-Identifier: AGPL-3.0-or-later
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using System.Runtime.InteropServices;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;

namespace DynamicPortrait.Rendering;

// A separate kernel batch prevents two views from sharing the same sorted command
// lists and allocation ring. Calls stay on the game's existing framework thread.
internal sealed unsafe class RenderOnlyBatch
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DevicePhase(Device* device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ManagerUpdate(RenderManager* manager, float delta);
    private readonly DevicePhase begin, end;
    private readonly ManagerUpdate update;
    private readonly RenderManager.Delegates.Render render;

    public RenderOnlyBatch(ISigScanner scanner)
    {
        begin = Marshal.GetDelegateForFunctionPointer<DevicePhase>(scanner.ScanText("E8 ?? ?? ?? ?? F3 0F 10 83 ?? ?? ?? ?? E8"));
        end = Marshal.GetDelegateForFunctionPointer<DevicePhase>(scanner.ScanText("48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 41 54 41 55 41 56 41 57 B8 30 43 00 00"));
        // Manager.Update accepts an explicit visual delta.
        update = Marshal.GetDelegateForFunctionPointer<ManagerUpdate>(scanner.ScanText("40 56 48 83 EC ?? 48 8B F1 44 0F 29 54 24 ?? 8B 89 ?? ?? ?? ?? 44 0F 28 D1"));
        render = Marshal.GetDelegateForFunctionPointer<RenderManager.Delegates.Render>((nint)RenderManager.MemberFunctionPointers.Render);
    }

    public void Submit(Action draw, Action marker)
    {
        var device = Device.Instance();
        var manager = RenderManager.Instance();
        if (device == null || manager == null
            || manager->Is3DRenderingDisabled || manager->InitializationFlags != uint.MaxValue)
            throw new InvalidOperationException("Graphics renderer is unavailable for an extra batch");
        // Revalidate the model before opening a batch; a redraw must not submit
        // an empty frame or change presentation order.
        draw();
        begin(device);
        try
        {
            // Never replay Scene.Manager.Update/PostUpdate here. Character
            // UpdateRender queues animation jobs even with a zero time delta;
            // a later model teardown can leave those jobs with a null skeleton.
            // Animation/game updates already ran. Zero delta avoids advancing
            // manager-owned visual timers a second time while rebuilding view data.
            update(manager, 0);
            render(manager);
            marker();
        }
        finally { end(device); }
    }

}
