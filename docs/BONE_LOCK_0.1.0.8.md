# 0.1.0.8: Bone stabilization and settings layout

## Behavior

**Lock bone in frame** is enabled by default, including when loading an older configuration without that field. The camera rig uses the selected bone's world position and rotation for its eye, look-at offset, and up direction. It bypasses world-space smoothing so animated translation and rotation do not introduce tracking lag. Other joints, facial expressions, and the background continue animating; the plugin does not freeze or modify skeleton poses.

Yaw, pitch, roll, distance, FOV, and offsets still control framing. Orientation and smoothing remain saved but are disabled while locked. Unlocking restores the existing character/bone/world orientation options and smooth following. Chest remains the default bone, the portrait window remains 340 × 380, and the output limit remains 4096 pixels. Reset all restores the lock and all other defaults.

The renderer rereads the subject pose after the original native camera matrix update, immediately before applying the portrait camera, instead of solving from the pose read before the extra tick. Subject identity and address are checked again. A subject, bone, orientation, or lock-mode change resets smoothing history. Repeated matrix updates in one portrait submission reuse its solved camera pose.

## Settings layout

The top toolbar holds start/stop, window visibility, and status. Camera, Output, Window, and Diagnostics have separate tabs in a scrolling content area. Reset all remains accessible below that area. Camera controls group subject/bone, tracking, and framing. Manual bone entry and near clip are advanced settings. Explanations use tooltips; diagnostics use a label/value table and hold the main-view-only mode switch.

## Validation

- Release build: zero warnings and errors.
- 30 camera/configuration checks, including 120 animated frames at three camera pitches. Multiple rigid head landmarks retain their projected positions despite translation, rotation, quaternion sign changes, different frame times, and unlocked orientation settings. Framing remains adjustable and unlocking restores smoothing. Older configurations enable the lock and reset all includes it.
- 62 offline ABI, scheduling, and D3D11 checks. Additional WARP tests render a rigid head surrogate through the real solver and capture path at three translated/rotated poses, verifying stable output pixels within an edge-rasterization tolerance.

These tests use synthetic geometry and no game process. They establish the stabilization math and GPU path, not FFXIV's actual animation/render timing or an in-game UI result. If animation advances again after the matrix hook, residual movement may remain and requires measuring that game-side ordering. The extra Framework tick and shared temporal histories remain unchanged. See [RUNTIME_VALIDATION.md](RUNTIME_VALIDATION.md) for running, turning, jumping, emotes, mode switching, and reset checks.
