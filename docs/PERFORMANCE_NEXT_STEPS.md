<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
# Performance limits and next steps

## Current evidence

The fixed 120 FPS experiment is reverted. Ordinary legacy portrait capture follows game frames. The user reports that lower frame rates reduce visible walking jitter, but neither FXAA nor changing submission order solves it. This correlation does not establish whether Active Camera, additional updates, or shared rendering state is responsible.

The main-first foreground report `render-investigation-20261003-114939.json` recorded median outer Tick duration of 16.55 ms without portraits and 32.87 ms with portraits. Task execution ran 298 times for 298 baseline ticks and 336 times for 167 completed comparison ticks. Mean task-call duration was about 6.8-6.9 ms. Manager rendering ran once inside each task sequence, about 1.8-2.0 ms per call. These are inclusive timings: task time includes rendering and must not be added to it again. The run exported frames and enabled detailed observers, so it is evidence of duplicated work rather than a clean performance benchmark.

Native SwapChain.Present at RVA 0x21B560 reads Device.FrameRateLimitPresetPresent and FrameRateLimitPresent before calling DXGI Present with a synchronization interval. Legacy portrait presentation is already suppressed before this original function. There is therefore no basis for claiming that simply suppressing another portrait Present would remove a second VSync wait. The remaining displayed main Present can still wait, and additional rendering can miss a refresh boundary. No wait or frame limiter was modified.

Output resolution is a crop size; the extra scene still renders at game resolution. Reducing the output slider saves copy/conversion/storage work, not a full low-resolution scene render. Resource counts and the observed capture-copy duration do not support blaming growing diagnostic counters for the dominant cost.

## Priority

1. Make the render-only path produce correct character transforms and visibility. It must reuse the normal frame's finished animation pose and prepare view-dependent character render data without queuing a second animation/game update sequence. The previous broad Scene.Update/PostUpdate replay crashed during redraw and is not an acceptable shortcut. The current render-only prototype still fails character-orientation validation and is not an available performance fix.
2. Separate portrait render targets and view-dependent temporal resources. A genuinely smaller render viewport reduces scene work; a smaller copied crop does not. This also prevents two views from consuming the same history, although its relation to the user's jitter remains unproven.
3. Profile individual normal and portrait task boundaries with detailed bone probes disabled and without frame exports. Identify duplicated UI/plugin callbacks separately from required character/render jobs before suppressing a task. Do not skip the entire task manager or mutate undocumented job-list links.

An asynchronous worker cannot safely call the current full Tick or renderer against shared native camera, scene, and command-context state. Parallel preparation becomes useful only after an independent submission path exists. Cadence reduction can save work immediately by submitting fewer portraits, but changes portrait refresh and is not part of this revert.

## Task profiling and UI reuse

The current feature build adds `-PerformanceOnly`. It observes the existing base Task.Execute invocation and aggregates time by the task's verified Func and root-list priority, separately for baseline, comparison main and comparison portrait. Each invocation forwards exactly once. It does not alter task links, job lists or function pointers. It disables bone, camera-state, orbit, UI3D and character-update probes. RenderView and render-batch flush observers add bounded aggregate timing; their inclusive durations overlap task and render timings and must not be summed with their parents. Native addresses are used only while hooked; copied labels/counters are serialized after sampling. All observer hooks are disabled at completion and disposed with the investigation.

The first reports (`120915`, `121040`) put the render task near 9 ms per call. Report `121302` instead records about 2.0 ms for the normal render task and 2.7 ms for portrait. These live conditions vary, so the first value is not a universal cost or a reliable isolated estimate of GPU work. UI interval update remains around 1.2 ms in these samples.

Native UIModule.Update at RVA `0x787880` advances its UI frame count and updates text, completion, macro/shell, UI3D and addons. It is separate from UIModule.Draw2D and AtkServer command processing. The new hook skips this update only in a legacy portrait Tick, after the normal Tick has already run it. Draw2D and the existing capture marker remain in place. Native scene, animation and render jobs still run. Main-view and idle UI updates always forward. Diagnostics has a session-only `Reuse main UI update` toggle, enabled by default, to disable the optimization for comparison.

Consecutive foreground, no-export tests with the same profiling hooks:

| Measurement | UI reuse off (`121539`) | UI reuse on (`121559`) |
| --- | --- | --- |
| Portrait interval UI task mean | 1.175 ms | 0.110 ms |
| Comparison outer Tick mean | 30.734 ms | 29.178 ms |
| Comparison outer Tick median | 33.202 ms | 28.763 ms |
| Main interval UI task mean | 1.290 ms | 1.263 ms |

The on run records 273 suppressed portrait UI updates, with normal updates retained, 273 completed captures and no recorded fault. The off run records no suppressed full UI updates. Transition and the final draining frame explain differences between outer tick, update and capture counts. Older reports call the total native update counter `MainUiUpdates`; it is renamed `UiUpdates` because control runs include portrait calls in that counter.

The supported local saving is about 1.06 ms of duplicate CPU work per portrait. Scene activity and display waits changed between finite runs; the outer Tick measurements do not guarantee a particular FPS gain. This does not remove the second full Tick, isolate GPU temporal history, or fix jitter.

Export validation report `121726` completed 165 captures with no fault. The main export retained game and plugin UI and the portrait export showed its separate angle. The subject was lying down with an occluding character, so these images do not validate face framing, UI interaction, or absence of visual jitter. All three reports are `render-investigation-20261003-HHMMSS.json` in the plugin diagnostics directory. The assembly version is unchanged and these changes have not been released.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Start-RenderInvestigation.ps1 -SecondsPerPhase 8 -RefreshLimit 0 -PerformanceOnly -ReuseMainUiUpdate Off -Foreground
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Start-RenderInvestigation.ps1 -SecondsPerPhase 8 -RefreshLimit 0 -PerformanceOnly -ReuseMainUiUpdate On -Foreground
```

The next larger optimization remains a portrait submission that reuses finished animation and performs only view-dependent scene work at its own target resolution. UI reuse is a smaller verified step, not a substitute for that path.
