# Render-only backend investigation

## Objective

Replace the second complete `Framework.Tick` with an additional scene-render submission. Game logic, input, networking, and animation should update once per displayed game frame. Moving the existing tick onto a worker thread is not the proposed solution.

## Initial evidence

Read-only inspection of the local game executable on 2026-10-02 resolved these signatures to unique destinations:

| Candidate | RVA | Source |
| --- | --- | --- |
| Existing portrait matrix hook | `0x260FA0` | Current portrait backend |
| `RenderManager.Render` | `0x2BA1F0` | FFXIVClientStructs Render/Manager.cs |
| `RenderManager.RenderView` | `0x2BABD0` | FFXIVClientStructs Render/Manager.cs |
| `TaskManager.ExecuteAllTasks` | `0x610C0` | FFXIVClientStructs Framework/TaskManager.cs |

Reproduce signature checks with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Verify-GameSignatures.ps1 -GameExe "PATH/TO/ffxiv_dx11.exe" -RenderCandidates
```

These checks read the executable file; they do not invoke functions, attach to the game process, or alter the game. RVAs are evidence for this inspected binary, not hardcoded runtime addresses or compatibility promises.

Local IDA metadata names separate framework tasks for camera update, scene update, scene post-update, render update, rendering, Havok animation, bone physics, and game updates. Static disassembly confirms:

- `TaskUpdateGraphicsRender` (`0xD4160`) reads `Framework.FrameDeltaTime` and calls a separate manager update function at `0x2B9C00`.
- `TaskRenderGraphicsRender` (`0xD4180`) tail-calls `RenderManager.Render`.
- `RenderManager.Render` accesses game thread-local graphics context state and changes command context metadata. It is not an isolated texture-rendering API.
- The beginning of `RenderView` schedules/waits for framework jobs, clears counters/list endpoints, and assigns context view indices. Reentry therefore cannot be assumed safe or sufficient to prepare a second view.

The legacy xivr-Ex task loop executes all tasks and uses fixed indices from an old client. The maintained FFXIV VR implementation still invokes two whole ticks. Neither provides a verified render-only backend to copy. Local View/SubView definitions also contain layout/version TODOs, so view indices and attachment fields need verification before use.

## Initial observation

### Automated live investigation

The dev plugin accepts a one-shot `render-investigation.request.json` in its Dalamud plugin configuration directory. Requests expire, are consumed on load, and never enable recurring profiling on ordinary startup. With automatic dev-plugin reloading enabled, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Start-RenderInvestigation.ps1 -Foreground
```

The running plugin polls the request file; sampling does not touch the Release DLL or trigger a reload. `-Foreground` temporarily brings the running game forward, waits for the report, and restores the previous foreground window if the user has not already selected another one. It does not send game input. Omit the switch to leave window focus unchanged.

The plugin samples 12 seconds of the normal tick followed by 12 seconds of the existing portrait backend, then stops rendering automatically. It temporarily bypasses portrait-window visibility for this finite comparison, while retaining loading/login/cutscene checks. It does not change saved settings, window geometry, or session diagnostic mode. The portrait phase still uses the existing second tick; observation hooks never replay additional native functions.

JSON reports are saved to `%APPDATA%/XIVLauncher/pluginConfigs/DynamicPortrait/diagnostics/`. They contain inclusive native call times/counts, bounded event traces for the first eight frames of each phase, frame pacing, foreground-frame counts, task function RVAs, process resource checkpoints, and portrait resource/capture counters. Timing includes engine waits and does not measure GPU execution. Event storage is capped at 4096 records; frame percentile samples are capped at 4096 per phase, while aggregate counters continue for the finite observation period. Hooks added solely for observation are disabled when sampling finishes. Serialization and file writes happen after sampling on a worker that never calls game APIs.

The first live background sample on 2026-10-02 showed normal Tick wall time of 87.0 ms versus 171.6 ms with portraits. Task execution and manager rendering were called approximately twice per displayed frame. Markers remained at zero and one texture set owned six textures, with no retired sets. The sample confirms duplicate work and duplicate frame waits; it does not establish a foreground performance budget or rule out GPU bottlenecks. The live task list also confirms separate game, camera, scene, animation, scene post-update, manager update, and rendering tasks. They must not all be replayed to produce an extra view.

Another complete background sample (`render-investigation-20261002-210453.json`, zero foreground frames in both phases) reproduced the result:

| Measurement | Baseline | Portrait |
| --- | --- | --- |
| Tick mean / p95 wall time | 64.66 / 66.83 ms | 126.98 / 129.24 ms |
| Observed outer ticks | 185 | 94 |
| Task execution calls | 185 | 189 |
| Manager rendering calls | 185 | 189 |
| Mean task execution per call | 10.51 ms | 9.77 ms |
| Mean manager rendering per call | 4.13 ms | 3.52 ms |

The portrait phase completed 94 copies. Its last CPU copy call took 0.023 ms; this does not include GPU completion or prove low GPU cost. Resource checkpoints reported zero pending markers, one texture set, six textures, zero retired sets, and 84.4 MiB of owned texture storage. A short sample cannot rule out every long-session leak. Much of the observed Tick time lies outside task execution and Present, consistent with frame waiting being repeated alongside updates. A foreground attempt changed focus between phases and must not be used as a matched comparison.

The baseline already observes one original Framework tick with bounded phase order, thread identity, counts, and wall time. Follow-up observation should resolve selected task boundaries and visibility preparation without replaying any game updates.

Determine when animation poses become ready, when camera matrices and visibility lists are consumed, where render jobs finish, and when UI and Present enter the command stream. Confirm actual task identities/order using resolved code addresses rather than an assumed task number. The observation mode must not replay tasks or change camera state.

## Render-only prototype

After the observed ordering and necessary state are understood:

1. Keep the normal full tick and its updates single-pass.
2. Prepare a portrait camera snapshot after animation completion and before the additional view's visibility/render work.
3. Submit only the required visibility and scene-render jobs through the game's existing scheduling context. Do not replay animation, networking, input, or every framework task.
4. Provide appropriate portrait render targets and isolate view-dependent camera/job/history state. Reusing an already-built main-view visibility list is insufficient for arbitrary angles.
5. Capture only the portrait output and display the most recent completed texture. Preserve normal UI and main-view Present behavior.

Begin with a fixed camera and low update frequency. Keep the existing backend available for comparison and the new backend explicitly selectable. Do not call `Manager.Render()` or `RenderView()` twice solely because their signatures resolve.

## Acceptance

- A displayed frame executes one full Framework tick and one game/animation update sequence, with only the intended additional render work.
- Main and portrait views show different angles, including a subject outside the main camera's visible set.
- Stopping or unloading restores camera/render state and drains in-flight resources.
- Redraw, territory change, UI, and Present remain correct.
- Compare normal/portrait/copy CPU times and frame pacing against the current backend in the same scene. Evaluate GPU cost separately; a render-only backend still incurs additional scene rendering.

Only after this prototype works should worker-thread command preparation or delayed presentation be considered. The engine's own render jobs are the first parallelism mechanism to investigate. Offline tests cannot establish safe game render-task reentry or the correct runtime ordering.

## Implemented experiment

Version 0.1.0.13 included the optional render-only prototype, which subsequently crashed in animation job execution. Version 0.1.0.14 removes unsafe scene replay. Bounded live runs no longer reproduce that fault, but actual frame exports fail character-orientation validation. RenderOnly is therefore investigation-only, and ordinary Start requests remain stopped. See [crash analysis and live results](RENDER_ONLY_CRASH_0.1.0.14.md). Observation hooks remain passive; only explicit finite requests activate the extra graphics submission.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Start-RenderInvestigation.ps1 -Backend RenderOnly -SecondsPerPhase 12
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Start-RenderInvestigation.ps1 -Backend RenderOnly -SecondsPerPhase 5 -CaptureLimit 3 -ExportFrames
```

`-RefreshLimit` applies a temporary capture cap (1-60). `-CaptureLimit` ends the portrait phase after the requested copies. `-ExportFrames` explicitly enables staging readback of actual game buffers, producing `main.png`, `portrait.png`, and, after at least two captures, `main-after.png`. Readback can stall the GPU; omit exports for performance comparisons. No saved settings are changed by these overrides.
