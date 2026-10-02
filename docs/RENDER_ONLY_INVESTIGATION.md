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

## Next experiment: Observe before replaying

Add an explicitly enabled observation mode that executes only one original Framework tick. Record bounded phase order, thread identity, call counts, and wall time for manager rendering and selected task boundaries. Use a bounded buffer and summarize after the frame; do not format a log entry for every native call indefinitely.

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
