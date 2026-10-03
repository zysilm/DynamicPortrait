# Main camera restoration defect

## Cause and evidence

The 0.1.0.15 timing correction did not resolve walking jitter. Its time-step measurements could not establish the visual symptom's cause.

A separate defect existed in CameraOverride: it saved and overwrote Scene.Camera.Position, LookAtVector, and ViewMatrix for portrait capture, then restored them at the next Render.Camera.SetMatrices call. The native Scene.Camera.UpdateRender function (RVA 0x41FB90 in the tested executable) computes a fresh ViewMatrix before passing its address into SetMatrices (RVA 0x260FA0). Restoring the old scene snapshot at this point also overwrote that input argument, replacing the freshly computed main view with stale data.

The input alias is verified in the native call sequence: the view field at scene + 0xA0 is passed as RDX to SetMatrices. A regression test reproduces the sequence by updating the scene camera after portrait Apply and passing the same field by address across Restore. It failed before the correction and passed afterwards.

`render-investigation-20261003-081355.json` recorded 286 main-view restore calls during the portrait phase. All 286 received the scene view field by address, and all 286 overwrote its incoming matrix. Rendering was manually activated during part of that run's baseline, so the baseline is not a valid disabled performance comparison. The per-call overwrite observations remain valid.

This gives a concrete mechanism for capture-frequency-dependent camera judder: the main camera's new result was rolled back whenever a portrait was captured. It does not establish that the character's authoritative position was being corrected.

## Correction in 0.1.0.16

Portrait Apply now changes only Render.Camera fields. It leaves the gameplay scene camera untouched throughout capture. Restore releases the render override without writing to Scene.Camera, so native camera tracking and the next SetMatrices input retain their latest values. No additional render-thread wait or native function hook is introduced.

Finite investigation reports include aggregate main-view restoration, input-alias and input-overwrite counts, plus at most 64 matrix samples. Normal rendering does not collect these samples. Main-frame image export now recognizes an unsuppressed Present even when the next portrait marker is already pending.

## Validation

- Regression test: fresh main camera view, position and look-at survive portrait restoration; portrait Apply leaves the complete scene camera unchanged.
- `render-investigation-20261003-081540.json`: 95 portrait submissions, 94 completed captures and main-view restore calls, 94 aliased inputs, **zero input overwrites**, no recorded fault. The exported portrait retains the intended front-facing character view.
- `render-investigation-20261003-081759.json`: final 0.1.0.16 build, 5 FPS investigation cap, 32 portrait submissions and completed captures, 32 aliased main-view inputs, **zero input overwrites**, no pending markers or recorded fault. Actual main-after and portrait images were exported and inspected; both retain their respective camera views.
- Release build and 130 offline camera/configuration, native ABI, scheduling and D3D11 checks pass.

The matrix corruption is fixed and observed in the game. Still images and matrix counts alone do not prove that every form of walking jitter, temporal rendering artifact, or dual-tick side effect is eliminated.
