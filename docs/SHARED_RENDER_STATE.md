# Shared render state investigation

## Symptoms and scope

The user reports walking jitter with CombatSimulator Active Camera, stronger aliasing without Active Camera, reduced frame rate, and flickering dialogue bubbles while portrait rendering is enabled. Normal camera-update counters do not establish visual correctness. The investigation now covers state shared by both submissions rather than only the orbit-center callback.

## Native evidence

Read-only disassembly of the installed executable identifies the main render camera's virtual slot 6 at RVA `0x25F4D0`. This function uploads camera constants and also changes persistent camera state:

- It updates projection offsets through the object at camera `+0x278`, advancing its sequence counter at `+0x10`.
- It uses the matrix region at camera `+0xA0..0x19F` during constant construction.
- At `0x260F10..0x260F72`, it copies the current view into camera `+0x120` and the current projection into camera `+0x160` for subsequent updates, unless the native flag at `+0x202` disables this path.
- It stores the previous projection offsets at camera `+0x288`.

CameraOverride restores the current view, projection, origin and selected lens parameters. It does not restore these history matrices or the projection-offset sequence. Thus preserving Scene.Camera and avoiding a duplicate gameplay-camera update does not isolate render history.

UI3DModule.Update at RVA `0x77AFB0` resets and rebuilds world-space UI lists. UIModule.Update calls it before UI draw-command processing. Skipping AtkServer.ProcessUICommandsAlt suppresses the portrait UI submission but does not suppress this upstream update.

RVAs and undocumented offsets describe the inspected binary only. They are not compatibility guarantees. No native memory is written by the new observation hooks.

## Live evidence

Finite probes observe camera constants and UI3D updates while forwarding each native original exactly once. They retain at most 2048 samples per observer, and are removed when sampling stops. Hashes are copied values; native addresses are not dereferenced by the report-writing worker.

`render-investigation-20261003-095236.json` recorded 41 portrait UI3D updates and 41 extra projection-sequence advances. Camera-history hashes changed in both views. This establishes additional persistent-state updates but does not by itself identify which history matrix reaches the main view.

`render-investigation-20261003-095428.json` added hashes of the specific previous-view and previous-projection matrices and sampled state immediately after CameraOverride.Restore:

- All 32 sampled main camera restorations still had a previous-view hash equal to the preceding portrait view hash.
- The stationary baseline had zero previous-view changes across 162 camera-constant calls.
- The portrait phase had 41 previous-view changes in portrait calls and 40 in main calls.
- Projection-sequence advances occurred 41 times in each view, compared with one advance per baseline Tick.
- UI3DModule.Update ran 41 additional times during portrait submission.
- The stop checkpoint recorded 40 completed captures and no rendering fault. The final in-progress submission explains the difference from 41 portrait updates.

These samples do not require walking or foreground focus to establish cross-view CPU history contamination. They are not frame-rate benchmarks or visual validation of bubble flicker. The probe does not observe temporal texture contents, final motion vectors, or a specific dialogue bubble's visibility transitions.

## Consequences and next boundary

Cross-view camera-history contamination is confirmed. Whether it accounts for each reported visual symptom remains unproven. Repeated UI3D updates are confirmed; their causal relationship to bubble flicker remains unproven.

A correction must preserve each view's camera history and projection sequence, and must also examine temporal render targets and UI update ordering. Restoring CPU matrices alone cannot undo a portrait image already written into a shared GPU history texture. Whole-object memory copies are unsuitable because the camera contains referenced resources and native pointers.

The legacy path still executes a second full Framework.Tick. Removing the visual-state contamination does not remove that cost. No performance improvement or flicker fix is claimed by these diagnostic additions.

## CPU isolation in 0.1.0.18

CameraOverride now snapshots and restores the four verified history matrices at `+0xA0..0x19F`, current and previous projection offsets at `+0x280..0x28F`, and the offset values and counter in the sequence object. Sequence restoration requires the same current sequence pointer; a replacement is preserved and the old allocation is not dereferenced. No constant-buffer pointer, reference count, vtable, or GPU resource is restored. Hook initialization checks that the main render camera's slot 6 matches the inspected native implementation before enabling the writable history path.

The regression test simulates native constant updates copying a portrait view into the previous-view field and advancing the sequence. It failed before the change. It now verifies history restoration, sample restoration, offset restoration, and preservation of replaced native resources and sequence allocations.

The main UI3D update hook now skips only legacy portrait calls. Main-view calls and calls outside the extra submission still forward. The finite observation hook may sit inside this hook chain, so zero observed portrait UI3D calls means the native path was skipped; checkpoint suppression counters separately record intercepted calls.

Live report `render-investigation-20261003-095824.json` tests CPU camera isolation alone: zero of 32 main restorations retained the preceding portrait view as history, compared with 32 of 32 before the change. Consecutive sampled main sequence counters advanced by exactly one.

Live report `render-investigation-20261003-100304.json` tests camera isolation plus UI3D suppression: zero of 32 main restorations retained portrait history, sampled main sequence increments remained one, and the observer recorded 80 baseline and 40 comparison main UI3D calls with no portrait native calls. The stop checkpoint recorded 39 captures and no fault. The game remained responsive.

Main-after and portrait frames were inspected. The main view retained its scene and UI. The subject was mounted and the portrait was largely occluded in both inspected runs; these images are not evidence of correct face framing or absence of flicker. Dialogue-bubble flicker and walking jitter still require visual validation. Shared GPU history textures are not isolated by this change, and the second complete Tick remains.

## Order experiment and spatial comparison

The user reports no improvement after the main-first ordering experiment in 0.1.0.19: walking jitter, aliasing and bubble flicker remain in the main view while the portrait remains stable. Reordering alone is therefore not a solution to the reported symptoms.

Version 0.1.0.20 adds a session-only, default-off Diagnostics option, `Use FXAA for both views (test)`. It scopes native GraphicsConfig.AntiAliasing to 1 and GraphicsRezoUpscaleType to 0 during each legacy rendering Tick, then restores the prior values in Dispose. It does not call IGameConfig.Set, change saved configuration, or change resource pointers. Both selections are required for the comparison: the inspected camera-constant function enables projection jitter when upscale type is 2 even when AA is not set to jittered TSCMAA. This experiment is not supported by the render-only backend.

Live report `render-investigation-20261003-115350.json` confirms AA=1/upscale=0 in 212 sampled main camera-constant calls and 214 portrait calls during the comparison; baseline calls recorded AA=3/upscale=2. Two transition main calls still used the baseline selections before the experiment was enabled during Framework.Update. The finite run ended without a recorded rendering fault. Offline checks cover restoration, exception unwinding, disabled behavior and preserving another callback's different selection. They do not establish that the visual symptoms improved, or that every GPU temporal pass was bypassed. Continuous visual feedback is needed to assess this controlled comparison.
