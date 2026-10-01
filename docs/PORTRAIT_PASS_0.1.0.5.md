# 0.1.0.5: Final portrait output and reset all settings

## Motivation

The user confirmed working main-view diagnostic output in 0.1.0.4, but independent portrait output remained black. Samples from the old `ToneAdjustSource` were zero RGB. Restoring the camera when the CPU tick returned also failed to account for asynchronous rendering completion.

## Implementation

- Compute the portrait crop and compensated projection using final backbuffer dimensions. Remove the old intermediate-texture copying code.
- Use the pre-UI queued command only to mark the portrait frame. Copy at its corresponding Present, then suppress that frame's presentation.
- Skip game UI submission through `ProcessUICommandsAlt` during the portrait tick. Normal ticks still submit UI.
- Retain the camera snapshot until the matching Present, or restore it before the same camera's next native `SetMatrices`. CPU tick return no longer triggers restoration.
- A pending portrait prevents recomputing or overwriting its frame ID and crop parameters. Present consumption preserves the matching frame ID.
- Default to the portrait camera, with explicit activation still required. Diagnostic mode copies only the main view and disables camera controls.
- **Reset all settings** and `/dportrait resetall` restore the subject, bone, every camera parameter, capture settings, and window size, position, and interaction options. They clear the locked subject and smoothing history, stop rendering, and leave diagnostic mode. Already-submitted frames drain through their existing markers; queue records are retained and GPU textures are not destroyed immediately. Both windows become visible and expanded. `/dportrait reset` continues to reset only the portrait window.

## Validation and limits

Release builds and offline checks cover resetting every public configuration field, matching frame IDs, asynchronous draining, restoring every native camera byte after an override, taking fresh snapshots for subsequent frames, DXGI backbuffer copying, cropping, GPU state restoration, and resize. They cannot establish when the game render thread actually reads camera data.

Final checks for this version: Release build with zero warnings and errors; 23 camera/configuration checks and 57 rendering checks passed.

The in-game effect of this version had not yet been verified. It does not claim that the second camera or flicker is fully fixed. An extra full Framework tick still executes, and TAA, exposure, and shadow histories remain shared. These engine states may still affect the main view.

For this version's in-game test, start with the then-default self subject and `j_kao`, change Yaw, Pitch, Distance, and FOV, then switch bones and subjects. Check for an independent portrait angle, a stable main view, and paired Captures/Suppressed presents. If black output persists, logs now include final-backbuffer samples and corresponding frame IDs.
