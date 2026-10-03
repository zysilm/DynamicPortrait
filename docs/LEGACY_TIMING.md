# Legacy simulation timing

The 0.1.0.15 timing change did not eliminate the user's walking jitter. The
measurements below establish clock behavior, not its role as the primary cause.
Subsequent investigation found a reproducible main-camera input overwrite;
see [camera restoration evidence](CAMERA_RESTORATION.md).

## Confirmed problem

The legacy backend called a complete Framework.Tick for the portrait, followed by another complete Tick for the main view. The current executable's Tick at RVA 0xD1A40 computes elapsed time from PerformanceCounterValue, updates that clock origin, and passes FrameDeltaTime into the task manager. Both submissions advanced simulation. The main tick therefore received only the interval since the portrait tick, rather than the interval since the preceding main tick. A manual capture limit made that split intermittent.

Read-only task observation in `render-investigation-20261003-075411.json` recorded portrait deltas of 34.928, 16.581, 16.637, 16.789, and 16.844 milliseconds. The following main ticks were missing those same intervals when compared with their main-to-main performance counter differences. This confirms a timing defect; it does not prove it is the only cause of visible walking jitter.

## Correction in 0.1.0.15

The portrait submission uses the native PauseFrameTicksCounter to obtain a zero simulation delta while still executing the full job pipeline. NextFrameDeltaTimeOverride is temporarily deferred, because a nonzero one-shot override takes precedence over pause. After submission, restore the original timing origin, simulation delta fields, rounding remainders, pause state, and one-shot override. The main Tick then recomputes the full elapsed interval. Native frame IDs remain monotonic because both submissions still own frame jobs.

Normal legacy capture follows outer game frames, subject to the existing completion fence and subject availability. It has no separate FPS timer and ignores the persisted RefreshRate value. Experimental render-only and main-view diagnostic capture retain their own refresh limits. Finite investigations can cap legacy submission for comparison; `-RefreshLimit 0` selects its normal synchronized behavior.

This remains a dual-tick backend. Extra rendering and native task overhead remain, and input/network or third-party callbacks can still execute twice. No performance improvement or complete removal of walking jitter is claimed.

## Live verification

- `render-investigation-20261003-075618.json`: 5 FPS cap, 39 portrait submissions, 38 completed captures at the automatic stop checkpoint, no pending markers or recorded fault. All sampled portrait task deltas were zero. The missing main elapsed interval fell to rounding error. Actual portrait and main-after images were exported.
- `render-investigation-20261003-075720.json`: synchronized capture, 387 portrait submissions and 386 completed captures at the automatic stop checkpoint, no pending markers or recorded fault. All 387 portrait submissions corresponded to the 387 normal ticks during capture. The game remained responsive.

- Final 0.1.0.15 run, `render-investigation-20261003-080009.json`: 285 portrait submissions, 284 completed captures at stop, no pending markers or recorded fault. All 64 sampled portrait task deltas were zero. Maximum sampled main simulation delta versus main-to-main elapsed time mismatch was 0.3 microseconds.

The character was in a sideways pose and the selected bone was Chest, so these images are not a continuous-walking validation. Background frame limiting and other active plugins affect observed performance. Reports include at most 128 native timing samples per phase and are written only after the finite investigation ends.

## Reproduction

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Start-RenderInvestigation.ps1 -Backend FullTick -SecondsPerPhase 12 -RefreshLimit 0 -ExportFrames
```

Compare consecutive main samples using `(current.PerformanceCounter - previous.PerformanceCounter) / PerformanceCounterFrequency` against TaskDeltaSeconds. For an unpaused game without an explicit simulation-speed override, these should agree within floating-point rounding, including when a portrait tick occurs between them. Simulation overrides and pauses intentionally make simulation time differ from real elapsed time.

## Main-first experiment in 0.1.0.19

The legacy path now runs the normal Tick before the optional zero-delta portrait Tick. It returns the normal Tick result and does not run a third Tick. The render-only backend retains its existing order. If the normal Tick unloads the plugin, no portrait trampoline is called afterward.

Native Present presents the preceding completed batch. The portrait marker remains pending across the outer Tick boundary: the next normal Tick captures and suppresses the previous portrait, and the following portrait Tick permits presentation of the completed main view. Capture identity remains marker-based rather than inferred from the current CPU Tick. Stopping rendering still drains the pending portrait through the next normal Present.

Live report `render-investigation-20261003-114939.json` contains 64 paired timing samples, all ordered normal then portrait. Every sampled portrait simulation delta is zero. The comparison phase submitted 168 portraits and completed 167 captures before automatic stop, with no recorded fault. Main and portrait exports show their respective views. These checks establish submission and capture ordering, not absence of walking jitter; continuous visual feedback is still required. The second complete Tick and shared GPU temporal resources remain.

## Fixed 120 FPS experiment in 0.1.0.21

The user reports that the FXAA comparison does not solve the symptoms, but lower frame rates reduce visible jitter. For the requested comparison, legacy CaptureDue now uses a fixed minimum interval of 1/120 second rather than allowing capture on every outer frame. This also takes precedence over finite investigation refresh requests in this test build. It is a capture ceiling, not an independent 120 Hz render loop: outer game frames and pending capture completion still limit submissions. A frame arriving before the interval elapses is skipped. Render-only scheduling and saved RefreshRate configuration are unchanged. Main-first ordering remains. No visual improvement is claimed.

The user reported no improvement and requested reverting this experiment. CaptureDue has been restored to game-frame-synchronized legacy refresh, including the existing finite-investigation override. The assembly version is unchanged; this is a local feature-branch build, not a release.
