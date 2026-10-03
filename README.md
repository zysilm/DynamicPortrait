# Dynamic Portrait

An experimental FFXIV Dalamud plugin that displays a camera following a character bone in a separate in-game window. Choose a character and bone, and adjust camera angles, distance, FOV, smoothing, and window size.

## Installation

In `/xlsettings`, open **Experimental** → **Custom Plugin Repositories** and add:

```text
https://raw.githubusercontent.com/zysilm/DynamicPortrait/main/pluginmaster.json
```

Save, then search for **Dynamic Portrait** in `/xlplugins` and install it.

## Usage

Use `/dportrait` to open settings and click **Start rendering**. Rendering starts disabled whenever the plugin loads.

The default subject is your character's **Chest** (`j_sebo_c`). **Lock bone in frame** is optional and defaults to off. Enable it for immediate bone tracking: it calibrates against the selected orientation, then follows changes in bone position and rotation without smoothing lag. Choose **Head** for a stabilized head camera. Facial expressions and other deformation still animate. Unlock to use ordinary orientation and smoothing controls. Model redraws temporarily pause capture and resume when the new model is ready.

Settings are organized into fixed **Camera**, **Output**, **Window**, and **Diagnostics** tabs; only the selected tab's contents scroll. The output's longest edge defaults to 1024 pixels and supports up to 4096; the default window size is 340 × 380. Diagnostic mode copies the main view and disables camera controls.

**Full tick (legacy)** remains the default and the usable portrait backend. Its refresh follows game frames automatically. The additional portrait tick uses the native simulation pause, then restores the timing origin so the main tick consumes the complete elapsed interval. The portrait overrides only the render camera, preserving the gameplay scene camera and its freshly computed main-view matrices. The portrait tick also skips a second main gameplay-camera follow update, so bone-orbit camera plugins such as CombatSimulator Active Camera are driven by the normal tick. These corrections do not remove the cost of the additional tick. **Render only (experimental)** is currently restricted to explicit finite investigations: live frame exports show incorrect character orientation even though the background uses the portrait camera. Ordinary Start requests for that backend remain stopped with an explanatory status. Version 0.1.0.14 removes the prototype's unsafe scene update replay after an animation-job crash. The candidate reuses completed animation poses and submits a separate graphics batch without a second complete game tick, but it still waits for render-thread command consumption and has no independent camera history or render target. Both paths render at the game's resolution before cropping. See [crash analysis and live results](docs/RENDER_ONLY_CRASH_0.1.0.14.md).

- `/dportrait on` / `/dportrait off`: start or stop rendering.
- `/dportrait toggle`: show or hide the portrait window.
- `/dportrait reset`: reset the portrait window.
- `/dportrait resetall` or **Reset all settings**: restore settings, clear the locked subject, and stop rendering, preserving both windows' positions and sizes. Use **Reset window** or `/dportrait reset` to reset portrait geometry separately.

Legacy rendering runs the normal game tick before the portrait tick and reuses the normal tick's UI updates by default. Diagnostics includes a session-only **Reuse main UI update** toggle for comparison. Short in-game tests measured about 1 ms less duplicate CPU work per portrait; this is not a guaranteed FPS increase. The second full tick still adds substantial rendering cost. Main-view jitter, aliasing, and dialogue-bubble flicker remain unresolved. See [performance evidence](docs/PERFORMANCE_NEXT_STEPS.md).

## Build and release

Requires .NET 10 and Dalamud API 15.

```powershell
dotnet build DynamicPortrait/DynamicPortrait.csproj -c Release
```

The DLL is at `DynamicPortrait/bin/Release/DynamicPortrait.dll`. On Windows, the default Dalamud directory is `%APPDATA%/XIVLauncher/addon/Hooks/dev`; set `DALAMUD_HOME` to use another location.

Following CombatSimulator's workflow, pushes to `main` build and publish a `v<version>` release and update the custom repository index. Update the version in both the project file and plugin manifest before releasing a new version. Documentation-only commits can use `[skip ci]`.

## License

[AGPL-3.0-or-later](LICENSE). Rendering references [FFXIV VR](https://github.com/WesleyLuk90/ffxiv-vr); camera and CI references [CombatSimulator](https://github.com/zysilm/FFXIV-CombatSimulator). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
