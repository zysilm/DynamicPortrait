# Render-only prototype crash and candidate correction

## Evidence

The 0.1.0.13 prototype produced actual game portraits and, after presentation-order correction, exported a main frame with the original camera and a separate portrait window. A 12-second background comparison recorded median outer Tick wall time of 77.48 ms versus 129.65 ms for the legacy backend, with no additional complete Tick. These short samples did not establish stability. The subsequent 30-second portrait test crashed about six seconds into its portrait phase. The performance result must not be treated as validation of a usable renderer.

The local crash log is `dalamud_appcrash_20261003_000621_103_13416.log`:

- Native access violation `C0000005` at game RVA `0x1847C4A`, reading address `0xFC`.
- The faulting instruction is `cmp byte ptr [rbx+0xFC],0`; `rbx` is zero. The preceding instructions obtain the animation object from the job argument and read its skeleton pointer at `+0x10`.
- The crashing worker stack includes `0x185CCDC`, framework job execution, and `TaskManager::JobPool::InnerThread.Run`.
- The main thread is waiting inside `0x1847A50`, through normal `TaskManager.ExecuteAllTasks` and `PortraitRenderer.TickCore`. It is not failing in D3D11 texture copying or managed diagnostics serialization.

## Unsafe replay

The prototype invoked all scene objects' `UpdateRender` by replaying Scene.Manager.Update (`0x41E3C0`) with a temporary zero `Framework.FrameDeltaTime`. Static disassembly verifies this path:

```text
Scene.Manager.Update
  -> scene-object virtual UpdateRender
  -> Human.UpdateRender (0x440C40)
  -> CharacterBase.UpdateRender (0x4323A0)
  -> animation registration (0x1845FD0 -> 0x18478E0)
  -> thread-local animation job list
```

Animation registration is controlled by object state, not the time delta. It appends jobs even when delta is zero. Replaying scene preparation therefore does not merely rebuild graphics data: it also duplicates animation work and can carry an object into the next normal task pass after its skeleton has been detached. This is the leading explanation for the observed null skeleton. The log proves the fault and the code proves the unsafe registration path; it does not show the exact instruction that cleared this particular object's skeleton.

The render-thread completion fence only protects consumption of graphics commands. It cannot drain, validate, or own these animation jobs. Suppressing a Present or adding a managed try/catch cannot make this native worker fault safe.

## Correction in 0.1.0.14

- Remove all scene-manager Update/PostUpdate replay, scene-object iteration, and temporary global frame-delta changes.
- Prepare only the selected scene camera through its SDK `UpdateRender` virtual dispatch. The inspected camera implementation calculates its view and calls native SetMatrices; it does not dispatch character updates or register animation jobs.
- Keep zero-delta Render.Manager.Update, the extra graphics batch, render-thread capture acknowledgement, and the original normal Tick once per outer frame.
- Keep the legacy backend as default. Ordinary plugin loads do not enable rendering or a profiler; investigation requests remain expiring and one-shot.

The corrected backend has passed Release compilation and 125 offline scheduling/camera/configuration/D3D11 checks. Those checks cannot prove native job lifetime or portrait correctness in the running client.

## Live correction tests

After the client was restarted, a 30-second baseline plus 30-second portrait investigation completed 399 render-only captures with zero additional portrait Ticks, zero pending markers at completion, one texture set, six live textures, no retired sets, and no recorded rendering fault (`render-investigation-20261002-222816.json`). This is a bounded stability observation, not proof of long-session safety.

A second 30-second baseline plus 30-second portrait run after removing the unsuccessful experiments completed 396 copies (`render-investigation-20261002-225438.json`), again with zero extra portrait Ticks, zero pending markers, six live textures, one allocation set, and no rendering fault. The client remained responsive after automatic stop. These runs were background-limited; they do not establish foreground FPS improvements or stability during model replacement.

An intermittent 5 FPS cap investigation, with 12 seconds per phase, completed 47 captures without a rendering fault or pending marker (`render-investigation-20261002-225604.json`). Rendering stopped automatically. The configured cap is a maximum; the actual rate was lower in this background-limited sample.

The final legacy-backend smoke test completed three captures with no fault or pending marker (`render-investigation-20261002-225639.json`). Its actual game export, `frames-20261002-225634/portrait.png`, still shows the expected face close-up. The client stayed responsive, and all finite investigations stopped rendering automatically.

Real GPU frame exports fail image validation. With almost identical recorded bone and camera poses, the legacy capture shows the character's face while camera-only rendering shows the back of the head. Background framing follows the portrait camera. The comparisons are `frames-20261002-223041/portrait.png` (legacy) and `frames-20261002-222958/portrait.png` (candidate). These are running-client frame exports, not standalone test renders.

Two narrower experiments did not repair the picture: selected CharacterBase post-update draw-buffer refresh, and moving the extra graphics batch after the normal Tick. Both were removed. A further candidate draw-constant function (`0x42ECC0`) could not run because this character did not have its optional buffer allocated; validation stopped the investigation before invoking it. That experiment and its temporary native pointer diagnostics were also removed.

The candidate is not ready for ordinary portraits or a performance-success claim. Ordinary Start requests for RenderOnly stay stopped with an explanation; only expiring, explicit finite investigation requests can activate it. The legacy backend remains usable and default. Do not restore whole-scene replay to repair character matrices.

## Required live validation

Restart the game with rendering initially stopped. First run a short explicit RenderOnly probe with frame exports, checking both portrait composition and main-camera restoration. Then run a bounded continuous probe without exports. Confirm there is no extra TaskManager execution, no animation-job crash, no growing marker/texture counts, and no accumulated failure after automatic stop. Follow with an intermittent capture rate and model redraws. If camera-only preparation cannot supply correct actor draw data, do not reintroduce whole-scene replay to repair the picture.
