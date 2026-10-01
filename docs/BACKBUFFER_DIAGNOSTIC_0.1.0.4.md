# 0.1.0.4: Bypass the empty intermediate texture

## In-game confirmation (2026-10-01)

The user confirmed that the default diagnostic mode in 0.1.0.4 displays the game image in the portrait window. This validates the path from the final backbuffer to the window. Camera controls have no effect, as expected when diagnostic mode copies the main view. The second camera remains unresolved; this confirmation does not establish a working independent portrait camera.

## Changes and validation

In-game logs from 0.1.0.3 showed that the sampled `ToneAdjustSource` crop had alpha 0..0 and maximum RGB zero in both main-view diagnostic mode and second-camera mode. The user confirmed that diagnostic mode stopped flickering, while the second camera caused text and 3D flicker in the main view. Both portrait outputs were black. Making alpha opaque addressed transparency but did not fix the source of the black image.

Diagnostic capture now calls `IDXGISwapChain.GetBuffer(0, ID3D11Texture2D)` immediately before the original main `SwapChain.Present`, then copies a center crop of the actual backbuffer. It no longer reads `ToneAdjustSource` or submits a pre-UI marker for main-view diagnostic capture. The copy includes game UI and may include other overlays; it is not an independent second camera.

Each copy releases the COM reference acquired by `GetBuffer`. Backbuffers are not retained across resize. The first frame records pixel samples, and the existing five-second summaries remain. Bounded event tracing adds tick entry/exit, matrix application, and each Present to investigate the still-unresolved second-camera timing.

A read-only inspection of the local game executable's Present implementation found that it reads the DXGI object at `SwapChain+0x68` and calls `vtable+0x40` (Present). No game file or process was modified.

Offline validation adds a real WARP DXGI swap chain on a hidden Win32 window. Checks cover `GetBuffer` color copying, cropping, alpha conversion, releasing references before `ResizeBuffers`, and reacquiring the backbuffer. The window was not shown and the game process was not accessed. All 47 rendering checks passed; in-game confirmation remained necessary.

To validate this version, reload 0.1.0.4, leave **Diagnostic: capture main view only** selected, and enable rendering for approximately five seconds. A center crop of the main game view establishes final-image copying. It does not resolve the second camera, pre-UI capture, or shared engine state.
