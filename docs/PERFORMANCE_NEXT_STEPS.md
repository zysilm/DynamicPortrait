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
