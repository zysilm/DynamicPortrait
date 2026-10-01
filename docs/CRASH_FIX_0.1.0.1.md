# 0.1.0.1 crash investigation and offline validation

## Evidence

The 2026-09-29 23:50:48 dump reports an access violation at game RVA `0x22B9CD`, inside `ImmediateContextDX11.DoSetTargetCommand`, reading address `0x4C`. Its caller is `DynamicPortrait.Rendering.PortraitRenderer.RenderTarget`. Offset `0x4C` is `Texture.Height3` in the matching client structs.

Immediately before the crash, the plugin reported that capture/Present had not completed within the portrait CPU tick. Version 0.1.0.0 then cleared the command lookup. Its queued marker was a zero-target binding with a null first texture; once the lookup was gone, the render hook forwarded that marker to the native handler. The claim that this marker was a safe no-op was wrong.

The same log also reports Dalamud's hook verification warning: the plugin supplied `void(long, long)` for `SwapChain.Present`, but the actual contract is `void(SwapChain*)`. This was a separate ABI defect, not the immediate null-target crash location.

## Changes

- Present, tick, UI and target-binding callbacks use typed contracts checked against the host's generated delegate metadata; known functions use FFXIVClientStructs-resolved addresses.
- A pending portrait is tracked independently of CPU tick return. The renderer permits one outstanding portrait, consumes its capture on the render thread, and suppresses one corresponding Present.
- Failure/timeout no longer discards an already submitted request. It can still drain after capture is stopped.
- The marker contains a real scene render target. If the registry is missing during unload, it never passes a null target[0] to the original native handler.
- The plugin installs no hooks on load. Hook installation occurs on the framework thread after explicit start.
- The D3D11 texture implementation is independent of game singletons, allowing the exact production capture code to run in an external test process.

## Offline tests

```powershell
dotnet run --project Tests/Rendering/Rendering.csproj -c Release -- artifacts/offline-render
```

This program does not open or start FFXIV. It uses game assemblies only for struct/delegate metadata and stack-allocated test data; it never calls native game functions. A real D3D11 WARP device rasterizes a colored cube from two camera poses with reversed-Z depth, and the plugin's capture code creates the portrait shader-resource view.

Checks include:

- Exact delegate parameter and return-type matches for four known native contracts.
- Delayed render-thread completion, timeouts, stale frame IDs and exactly one suppressed Present.
- The crash's `target[0]->Height3` access remains valid even after clearing the registry.
- Every cropped pixel matches its source pixel, and capture does not change the source.
- Drawing the next main view cannot overwrite the saved portrait.
- Triple-buffer reuse, multiple aspect ratios/resizes, oversized input rejection and disposal.

The local run passed **35 checks** and the D3D11 device-health check. It produced `artifacts/offline-render/main-view.png` and `portrait-view.png`; both were visually inspected. The separate camera/configuration test program covers 22 mathematical checks.

These results validate the reusable rasterization/capture code and the specific command-lifetime regression. They do not prove that FFXIV's full rendering pipeline, engine-owned texture lifetime, culling, postprocessing or other plugins are compatible with this backend. A post-fix game run is still required for that conclusion.
