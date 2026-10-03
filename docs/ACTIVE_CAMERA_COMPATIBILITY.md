# Active Camera compatibility

## Report and code path

The user isolated the remaining walking jitter to CombatSimulator Active Camera. CombatSimulator hooks the gameplay camera orbit-center function. Its GetCameraPositionDetour reads the configured player bone via GetSyncedPoseModelSpace and replaces the orbit center; Camera.Update then updates native camera-follow state. Framework.FrameDeltaTime == 0 does not prevent this hook or these state updates.

Legacy portrait capture previously called a complete Framework.Tick twice. That called the main gameplay camera update twice, including CombatSimulator's orbit hook. Preserving Scene.Camera matrices and correcting simulation timing did not prevent this additional gameplay-camera update.

The local CombatSimulator source was inspected without editing its working tree. Its saved configuration had Active Camera enabled with j_sebo_a and collision/fade overrides. The latest corresponding runtime log entry was ActiveCamera ON. These establish the test context, but are not continuous observation of that plugin's settings.

## Correction in 0.1.0.17

DynamicPortrait hooks the current main gameplay camera's Update function (virtual slot 3). During the legacy portrait submission, calls for that same main camera return without updating gameplay follow, collision or orbit-center history. The normal game Tick still forwards the update exactly once. Calls outside the portrait submission, other camera objects, and investigation-only render batches forward normally.

Native input/state sampling is retained. The normal scene-camera render update later in the same full Tick still constructs render matrices, allowing the existing portrait matrix override and capture marker to run. No extra native scene update, graphics wait, cross-plugin IPC, or CombatSimulator modification is needed.

This changes only the duplicate gameplay-camera update. Other native tasks and plugin callbacks may still execute during the additional full Tick.

## Measurement

Finite reports now contain gameplay-camera update counts, suppression counts and aggregate inclusive wall times. `-ReplayGameplayCamera` temporarily restores the duplicate update for a bounded comparison; it is not a normal GUI setting.

`render-investigation-20261003-083342.json` recorded 62 extra portrait camera updates and 63 normal updates during the old-behavior phase. The portrait camera update averaged 0.033 ms, while the normal camera update averaged 0.031 ms. This sample does not support attributing most of the overall performance loss to Active Camera. It is not a moving-character benchmark or a direct measurement of bone synchronization alone.

The final 0.1.0.17 run, `render-investigation-20261003-083742.json`, recorded 63 portrait gameplay-camera calls, all 63 suppressed, while normal calls continued to forward. The suppression path averaged 0.001 ms and did not invoke an additional scene-camera update. The automatic stop checkpoint recorded 64 portrait ticks and 63 completed captures, zero pending markers, no main-view input overwrites and no recorded fault. The report checkpoint occurs during a native task callback, so the last in-progress submission is not a completed capture at that point. Main-after and portrait PNGs were exported and inspected, retaining their distinct camera framing. The game remained responsive.

Release compilation succeeded with zero warnings and errors. The offline render suite passed 93 ABI, scheduling and D3D11 checks, including the new CameraBase.Update delegate contract. Native camera-follow suppression itself is verified by the live counters, not by these standalone image tests.

The fix is intended to prevent duplicate camera-follow state updates. Continued walking remains the relevant visual acceptance test; frame exports alone cannot prove the absence of jitter. The legacy backend still submits a second complete game Tick and scene render.

## Reproduction

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Start-RenderInvestigation.ps1 -Backend FullTick -SecondsPerPhase 8 -RefreshLimit 0 -ReplayGameplayCamera -ExportFrames
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Start-RenderInvestigation.ps1 -Backend FullTick -SecondsPerPhase 8 -RefreshLimit 0 -ExportFrames
```

Run each finite test to completion before requesting the next. Compare portrait gameplay-camera calls and suppression counts, normal forwarding counts, fault status and image framing. Background frame caps affect total frame time; neither camera-update wall times nor still images establish a foreground performance gain.

## Remaining walking jitter

The user reported that 0.1.0.17 still jitters with Active Camera enabled. Its suppression counts therefore do not establish that duplicate camera updates were the visual root cause.

A direct, finite observation hook on the orbit-center function was added for investigation. The address is resolved from the same set-look-at signature and adjacent vtable slot used by CombatSimulator. It forwards its original exactly once and records at most 128 samples per phase, including center, camera history, target actor position, draw-object position and skeleton root position. Native object pointers are read only during the original callback and are not retained. Samples are copied to plain System.Numerics vectors to avoid serializing native vector helper properties; named non-finite floating-point values are allowed in diagnostic reports.

`render-investigation-20261003-091550.json` completed 62 portrait ticks and 61 captures. The orbit hook recorded 125 baseline calls and 62 main-view calls during capture, with zero portrait calls. `render-investigation-20261003-091812.json` repeated the result and found exact agreement between actor, model and skeleton-root positions in all sampled callbacks. However, the actor did not move in either phase, so these results do not validate walking or eliminate a moving-pose timing mismatch. A moving-character sample is still required before proposing another compatibility fix.
