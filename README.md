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

The default subject is your character's **Chest** (`j_sebo_c`). **Lock bone in frame** is enabled by default: the camera follows the bone's position and rotation immediately, keeping its rigid pose fixed in the portrait while the rest of the character and scene move. Choose **Head** for a stabilized head camera. Facial expressions and other deformation still animate. Unlock to use character/world orientation and smoothing.

Settings are organized into **Camera**, **Output**, **Window**, and **Diagnostics** tabs. The output's longest edge defaults to 1024 pixels and supports up to 4096; the default window size is 340 × 380. Diagnostic mode copies the main view and disables camera controls.

- `/dportrait on` / `/dportrait off`: start or stop rendering.
- `/dportrait toggle`: show or hide the portrait window.
- `/dportrait reset`: reset the portrait window.
- `/dportrait resetall` or **Reset all settings**: restore every default, clear the locked subject, and stop rendering.

## Build and release

Requires .NET 10 and Dalamud API 15.

```powershell
dotnet build DynamicPortrait/DynamicPortrait.csproj -c Release
```

The DLL is at `DynamicPortrait/bin/Release/DynamicPortrait.dll`. On Windows, the default Dalamud directory is `%APPDATA%/XIVLauncher/addon/Hooks/dev`; set `DALAMUD_HOME` to use another location.

Following CombatSimulator's workflow, pushes to `main` build and publish a `v<version>` release and update the custom repository index. Update the version in both the project file and plugin manifest before releasing a new version. Documentation-only commits can use `[skip ci]`.

## License

[AGPL-3.0-or-later](LICENSE). Rendering references [FFXIV VR](https://github.com/WesleyLuk90/ffxiv-vr); camera and CI references [CombatSimulator](https://github.com/zysilm/FFXIV-CombatSimulator). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
