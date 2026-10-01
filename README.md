# DynamicPortrait

An experimental Dalamud plugin intended to render an independent bone-following camera into an in-game portrait window. Licensed under **AGPL-3.0-or-later**.

**0.1.0.4 status:** the user has confirmed in-game that the default diagnostic mode displays the main camera's final backbuffer in the small window. This includes game UI and is not an independent camera. Bone, subject and camera-angle controls do not affect this mode. The experimental second-camera path still produces a black portrait and main-view flicker; it is not working yet. See [in-game findings](docs/BACKBUFFER_DIAGNOSTIC_0.1.0.4.md).

## Build and load

Requires .NET 10 and Dalamud API 15. Like CombatSimulator, the Dalamud SDK uses `%APPDATA%/XIVLauncher/addon/Hooks/dev` on Windows. Set `DALAMUD_HOME` only when using another distribution directory.

```powershell
dotnet build DynamicPortrait/DynamicPortrait.csproj -c Release
dotnet run --project Tests/CameraMath/CameraMath.csproj -c Release
dotnet run --project Tests/Rendering/Rendering.csproj -c Release -- artifacts/offline-render
```

DLL: `DynamicPortrait/bin/Release/DynamicPortrait.dll`

SDK package: `DynamicPortrait/bin/Release/DynamicPortrait/latest.zip`

Add the DLL to Dalamud's developer plugin locations. Keep the dependency DLLs beside it; for distribution, use the complete ZIP. The plugin does not require FFXIV VR, SteamVR, OpenXR or a headset.

## Use

- `/dportrait`: open settings.
- `/dportrait on` / `off`: start / stop rendering.
- `/dportrait toggle`: show / hide the window.
- `/dportrait reset`: recover a hidden, locked or off-screen window.

Rendering starts **disabled on every plugin load**, with `Diagnostic: capture main view only` selected. Enable rendering to display the main camera's final output. Window size and capture settings apply; subject, bone, yaw/pitch/roll, distance, FOV, local offset and smoothing are for the unfinished second-camera path. Right-click the portrait for settings. Position, size and camera settings are saved; locked character identity is session-only and clears on territory changes.

Since 0.1.0.1, no native hooks are installed until rendering is explicitly started. The startup/capture crash in 0.1.0.0 is addressed by correcting the Present ABI, retaining asynchronous capture requests until completion, and replacing the invalid zero-target marker. See [crash analysis and offline tests](docs/CRASH_FIX_0.1.0.1.md).

The bone list covers the model's body/root partial skeleton. Attached weapons/accessories are not yet supported. Bone names differ across models; missing bones show a status instead of dereferencing an invalid index. Bone orientation uses the skeleton's native axes, so different bones may need different angles.

## Current renderer and limits

The default diagnostic backend copies the actual DXGI backbuffer before Present, without an additional tick or camera override. It converts the private copy to opaque RGBA8 for ImGui display. It can include game UI and other overlays; HDR tone mapping is not implemented.

The unfinished **second-camera backend** is adapted from the [FFXIV VR](https://github.com/WesleyLuk90/ffxiv-vr) pipeline:

1. At the configured refresh interval, run a portrait Framework tick with overridden camera matrices.
2. Queue a marker before game UI drawing; the render thread copies the centered scene region into a GPU texture.
3. The render thread suppresses the portrait Present when it reaches the queued capture. CPU tick return does not imply GPU/render-thread completion. Camera fields are restored after CPU submission.
4. Run the normal tick and display the portrait texture through ImGui.

It is **not yet a render-only extra pass**: each portrait update adds an entire game tick. Input, animation timing, other plugins' framework callbacks and frame pacing may be affected. The camera's global rendering histories are not isolated; TAA, DLSS, FSR, auto-exposure, shadows and culling need in-game verification with substantially different camera angles. The plugin does not change your global graphics settings. Do not enable FFXIV VR concurrently with this backend.

The output resolution controls a centered crop with a compensated projection. The world still renders at the game's resolution; reducing the portrait texture size does not proportionally reduce scene rendering cost. The refresh limit is an upper bound, not a promised frame rate. CPU tick duration in diagnostics is not a GPU timing measurement.

Loading, cutscenes and GPose pause capture. Closing/collapsing the portrait pauses capture after a short visibility timeout. Signature or managed capture failures stop rendering and appear in settings / Dalamud logs. Native access failures cannot be made safe by managed exception handling alone.

Release compilation and automated camera-math checks do **not** establish in-game compatibility. See [runtime checks](docs/RUNTIME_VALIDATION.md) before calling a build stable.

The Windows-only `Tests/Rendering` program runs real D3D11 WARP (the software rasterizer) without a game process. It uses the plugin's actual camera and capture code to render two cube views, reads every cropped pixel back, checks main/portrait isolation, and exercises resizing and resource disposal. It also checks hook delegate contracts against the installed FFXIVClientStructs metadata and reproduces delayed command consumption. Output images are written to `artifacts/offline-render`. This tests the reusable D3D11 path, not FFXIV's internal render scheduling or temporal effects.

## Releases

CI builds branches and pull requests, with a separate Windows job for offline rendering regressions. Pushing a `v0.1.0.1`-style tag runs version checks, builds the Release package and creates a prerelease with the ZIP, license, notices and a `pluginmaster.json` asset. A tag matching `main` also updates the repository's `pluginmaster.json` after upload. The custom repository URL is:

`https://raw.githubusercontent.com/zysilm/DynamicPortrait/main/pluginmaster.json`

This URL becomes usable after the first release workflow succeeds. No release has been published by local builds.

Upstream attribution: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
