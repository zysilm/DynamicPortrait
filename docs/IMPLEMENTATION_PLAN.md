# DynamicPortrait implementation plan

## Experimental implementation status (2026-09-29)

The API 15 plugin, bone camera, portrait and settings windows, GPU scene capture, configuration, and release workflows are implemented and compile. The experimental backend follows FFXIV VR's double Framework tick approach: capture the portrait tick and suppress its Present, then display the normal tick. It does not yet satisfy the original single-logic-update requirement below. Temporal history isolation and in-game dual-view acceptance are also incomplete. Completing the project and compiling it does not establish those acceptance criteria.

See the root README for current behavior and [RUNTIME_VALIDATION.md](RUNTIME_VALIDATION.md) for in-game checks. The original plan below remains a reference for future optimization and stable-release acceptance. Its initial face-camera defaults and release-trigger suggestions are historical; the README describes current defaults and workflows.

## Goal and scope

Display a movable, resizable Dalamud window containing another live camera view of the same scene, initially framed toward the character's face. Normal main-view controls and rendering should continue to work.

The first version has one window, self/current-target/locked-subject selection, bone selection, camera angles and distance, framing offset, FOV, smoothing, window size, quality, refresh rate, and saved configuration. Keep the scene background. Transparent backgrounds, independent lighting, multiple views, recording, and automatic choreography are outside the initial scope.

The window is an in-game ImGui window. A separate operating-system window is not an initial goal.

## Reference implementations

Research date: 2026-09-29. The initial investigation began before this repository contained a plugin project.

### xivr-Ex

Repository: [ProjectMimer/xivr-Ex](https://github.com/ProjectMimer/xivr-Ex).

Local checkout: `C:/project/xivr-Ex`, commit `3e183e9b6007ac445226eee1cd7a6bee3fd765a1`.

- `xivr-Ex/xivr_hooks.cs:2934`: `FrameworkTickFn` calls the original Framework tick once per eye. This is not a ready-made independent RenderToTexture API.
- `RunGameTasksFn` traverses game tasks and inserts an eye marker before the final tasks.
- `RenderThreadSetRenderTargetFn` consumes that marker and calls native `SetThreadedEye`. View identity must travel with the command stream across threads.
- `xivr_main/dllmain.cpp:822`: `SetThreadedEye` uses D3D11 `CopyResource` to save scene color and depth; `RenderVR` rotates buffers.
- `CalculateViewMatrix2Fn` and `MakeProjectionMatrix2Fn` modify view and projection matrices respectively.
- The README targets Dawntrail / Dalamud 10 and notes performance problems and paused development. Old signatures, task indices, and offsets must not be copied into the current client.

Useful mechanisms include multi-view scheduling, command-stream markers, and GPU capture. The old double-tick implementation, VR dependencies, and offsets cannot simply be transplanted. An additional render-only entry point remains unverified.

### Maintained FFXIV VR reference

Repository: [WesleyLuk90/ffxiv-vr](https://github.com/WesleyLuk90/ffxiv-vr).

Local checkout: `C:/project/ffxiv-vr`, commit `bd5b4a9f6be472018411520c8fb4aae7cf43731e`, dated 2026-09-25; version 0.0.76, Dalamud SDK / API 15. Its README lists Dawntrail 7.x support. This is the preferred current VR reference, but there is insufficient evidence to describe it as an official ProjectMimer repository migration.

- `FfxivVr/game/GameHooks.cs`: `FrameworkTickDetour` still calls the original tick twice; `SetMatricesDetour` updates view matrices after the original function.
- `FfxivVr/vr/VRCamera.cs`: writes view, projection, and projection2 with explicit reverse-Z handling.
- `FfxivVr/vr/VRSession.cs`: switches eye phases and queues capture before UI rendering.
- `FfxivVr/vr/Resources.cs`: provides two `SceneRenderTargets` with shader resource views.
- `FfxivVr/vr/Renderer.cs`: `CopyTexture` copies each eye's game image into an independent GPU texture.
- `FfxivVr/vr/RenderManager.cs`: suppresses left-eye desktop Present and presents the right eye. It does not add a third ordinary main-camera view.

The working dual-view texture path provides a concrete basis for a main view plus portrait window. DynamicPortrait supplies the ImGui display; the VR project does not provide an independent portrait window out of the box. Replacing eye views with a main camera and bone camera, removing OpenXR session dependencies, isolating state, and handling culling at arbitrary angles still require development and validation. Two ticks alone prove neither doubled game speed nor one logic update: measure call counts and timing.

P0 should first use this project's current matrix injection, capture, and Present scheduling to locate entry points, then investigate an additional render-only path. Double ticks are a controlled research reference, not proof of meeting the single-update acceptance criterion.

The project declares `AGPL-3.0-or-later`; direct implementation reuse must account for this license. DynamicPortrait subsequently adopted that license.

### CombatSimulator

Local checkout: `C:/project/FFXIV-CombatSimulator`, HEAD `1dc1fff6466ff02af1e2cde8d854cbc8fb1c9eb2`. It had uncommitted changes and was not modified during the investigation.

- `CombatSimulator/Camera/ActiveCameraController.cs`: bone-name lookup, synchronized model-space poses, bone world positions, and close-range character visibility. It changes the main camera rather than rendering a second view.
- `CombatSimulator/Animation/BoneTransformService.cs`: references for `GetBoneNames`, `GetBoneWorldPos`, and `GetBoneWorldTransform`. Scale and partial-skeleton handling still need validation.
- `CombatSimulator/Camera/GameCameraView.cs`: reads actual view/projection matrices instead of guessing matrix direction and FOV conventions.
- `GameCameraUpdateHook.cs`: camera update timing and main-camera identification.
- The project uses `Dalamud.NET.Sdk/15.0.0`, with .NET 10 in workflows. Verify the target client and installed Dalamud instead of assuming compatibility with every client.
- `.github/workflows/build.yaml` and `release.yaml`: build, SDK packaging, GitHub Releases, and `pluginmaster.json` generation and updates.

CombatSimulator source files carry MPL-2.0 headers. Preserve applicable headers and obligations when reusing code. No LICENSE file was found in the inspected xivr-Ex checkout; use its mechanisms as references rather than transplanting large code sections.

## Technical approach

### 1. Validate additional scene rendering first

Identify the current client's scene-render task boundaries, camera state sources, resource bindings, and submission timing. The intended stable design updates logic once per frame and submits an additional scene render according to the portrait refresh budget, preserving normal game UI and Present counts.

Intended flow, with pass ordering determined by the prototype:

```text
Update game logic and animation once
  → Read target bones safely into an immutable camera snapshot
  → Submit the normal scene pass and a portrait scene pass when due
  → Publish the completed portrait texture to ImGui
  → Submit normal game UI / Dalamud UI / Present
```

First investigate whether the engine has a suitable additional-view submission path. Otherwise, validate narrowly scoped render-task reentry. Replaying the main view's final draw commands does not produce an arbitrary new view: visibility, LOD, constants, and shadows may already depend on the original camera.

If global engine camera state or intermediate targets must be borrowed, list every field accessed, limit the scope, restore CPU state, and ensure deferred GPU/render-thread commands retain valid data. Restoring matrices when the CPU call returns is insufficient.

The original stable-release gate excludes calling the complete Framework tick twice. If additional scene submission cannot be separated, document the blocking entry point and experimental results, then reassess the engine's offscreen character-preview path. That fallback may lose the live world background or current character state and must not silently be treated as equivalent.

### 2. Texture and render isolation

- Create and manage GPU textures on the same D3D11 device. Supply them to ImGui through the supported Dalamud texture interface, verifying the actual wrapping API during implementation.
- The prototype may use full-size engine intermediate targets and reduce the output afterward. Independent low-resolution scene targets require later performance validation; a small window alone does not imply low overhead.
- `CopyResource` does not resize. Use appropriate resolve, shader blit, or color conversion for differing dimensions or formats.
- At minimum, the portrait needs an independent final output. Evaluate depth, G-buffer, shadows, exposure, motion vectors, TAA, and upscaling histories individually for sharing, isolation, or disabling.
- Recompute or correctly extend visibility for the second camera. Validate characters behind the main camera and animation behavior when culled from its view.
- Carry frame/pass identity through tagged commands or an equivalent mechanism. A game-thread boolean cannot identify the render thread's current view.
- Use double buffering or a ring of output textures to order ImGui sampling, GPU writes, and destruction. Displaying the most recently completed frame is acceptable.
- Coalesce resize requests, enforce size limits, and stop submission before unload. Drain necessary in-flight use before releasing resources.

### 3. Bone following and composition

- The original plan defaults to `j_kao`, with common head, neck, and chest choices plus actual skeleton names. Not every model has the same bone names. The current default is Chest (`j_sebo_c`).
- Read model-space poses after animation updates and apply skeleton translation, rotation, and scale to obtain world positions. Validate partial skeletons, mounts, transformations, and special models.
- Separate the position anchor from the orientation reference. Following a head position relative to body facing avoids rotating the entire camera with small head motions. Offer bone-relative and fixed-world orientation alternatives.
- Parameters include yaw, pitch, roll, distance, local look-at offset, FOV, near clip, and position/orientation smoothing. Use actual engine projection conventions.
- Invalidate cached indices when the character instance, DrawObject, or skeleton changes. Pass value snapshots between threads rather than retaining pose pointers. Validate locked-subject identity against address reuse.
- Show a clear status for missing bones; fall back only to a verified anchor if needed. Stop sampling and additional submission during territory changes or subject disappearance.
- Limit close-range visibility and collision adjustments to the portrait pass and target. Do not reuse global forced-visibility behavior from reference plugins.

## Project structure

Keep one C# plugin project, favoring unsafe C# and suitable D3D11 bindings. Add a native helper DLL only for a demonstrated need; do not introduce OpenVR.

| Module | Responsibility |
| --- | --- |
| Plugin / Configuration | Services, commands, configuration versions, lifecycle |
| TargetResolver / BonePoseReader | Subject validity, skeleton enumeration, pose snapshots |
| PortraitCameraSolver | Composition math, smoothing, projection parameters |
| PortraitRenderBackend | Engine entry points, pass scheduling, state isolation |
| PortraitTextureBridge | GPU output, Dalamud texture wrapping, sizing, disposal |
| PortraitWindow / SettingsWindow | Image display and parameter UI |
| Compatibility / Diagnostics | Signature checks, compatibility status, timing, resource statistics |

Decouple the render backend from UI so the experimental path can be replaced. Avoid building a general multi-camera framework in advance.

## Implementation order and acceptance gates

### P0: Project and render probes

Create a minimal plugin, commands, and diagnostics. Record game, Dalamud, and FFXIVClientStructs versions. Locate render-task and thread boundaries, then establish GPU scene-texture display in the window. Copying the main view validates only the texture path, not the second camera.

Deliver entry-point/signature records, per-frame execution counts, texture sources/formats/dimensions, and candidate second-pass entry points.

### P1: A real second view

Generate an additional scene view using a fixed portrait angle or static second camera. Show it in a fixed-size window. Establish correctness before low-resolution optimization.

Required checks: different simultaneous main and portrait angles; normal movement, input, and animation timing; no repeated logic updates; subjects outside the main view still visible; no recursive UI capture; normal state restored after stop/unload. Test history contamination across anti-aliasing and upscaling settings.

The original plan deferred full settings UI and release automation until this gate passed. The current experimental implementation has those features but has not thereby passed the gate.

### P2: Usable first version

Add subject selection, initial head framing, bone selection, angles, distance, FOV, offsets, smoothing, window drag/resize/locking, and saved configuration. Supply parameters to rendering as immutable snapshots.

Original suggested starting values were approximately 320 × 320 display, 512 × 512 render target if supported, and 30 FPS. These are historical suggestions, not current defaults. Accumulate refresh time rather than skipping a fixed number of game frames. Stop additional rendering while the window is closed or hidden.

### P3: Performance and compatibility

Measure CPU/GPU cost with rendering disabled, enabled without refresh, at 15/30/60 FPS, and at different output sizes. Only then reduce portrait postprocessing, shadows, or scene content according to the measured bottleneck.

Cover territory changes, teleportation, subject destruction, outfits, skeleton rebuilds, mounts, death, window/game resolution changes, plugin reloads, and coexistence with other camera plugins. Initially pause in cutscenes, GPose, and login screens until behavior is validated.

Signature mismatches or initialization failures should disable the backend and show diagnostics. Managed exception handling does not make native memory access safe; validate versions, objects, and lifetimes before accessing them.

### P4: CI/CD and first test release

Follow CombatSimulator's SDK packaging and custom repository flow without its private modules or application-specific checks.

- PR/branch checks: restore, Release build, meaningful math/configuration migration tests, and build artifacts.
- The original proposal used version tags for releases. The implemented workflow instead releases from `main`, matching CombatSimulator. Verify assembly, manifest, tag versions, and API level; package SDK output, publish the GitHub Release, then update `pluginmaster.json`.
- Use immutable version download links. Publish assets before updating the installation index. Keep index updates idempotent and control concurrent updates.
- Pin SDK and dependencies. Record the Dalamud distribution used and check compatibility with its latest distribution. C# builds can use Ubuntu runners; adding a C++ DLL would require Windows builds and packaging checks.
- GitHub Actions verifies compilation and packaging, not in-process FFXIV dual-view behavior, resource lifetimes, or performance.

## Initial development conclusion

The feature set is small, but rendering boundaries and state isolation dominate the work in P0/P1. Establish an independently verifiable second view before extending bones and UI. Do not promise performance numbers or client compatibility before validating the prototype.
