// SPDX-License-Identifier: AGPL-3.0-or-later
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Runtime.InteropServices;

namespace DynamicPortrait.Rendering;

internal static unsafe class NativeCallbacks
{
    // Keep these ABI contracts separate so the offline tests can compare them against
    // the host's generated FFXIVClientStructs delegates without initializing the game.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CameraUpdate(FFXIVClientStructs.FFXIV.Client.Game.CameraBase* camera);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Ui3DUpdate(nint module);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool Tick(Framework* framework);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Present(SwapChain* swapChain);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SetTarget(ImmediateContext* context, RenderCommandSetTarget* command);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Ui(AtkServer* server, bool flag);
}
