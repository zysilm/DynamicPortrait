# 0.1.0.2: Candidate UI flicker fix

In-game feedback: enabling rendering caused both the settings and portrait windows to flicker.

Previously, `PortraitUi.Draw` read the CPU-side `inPortrait` flag and skipped all ImGui window submission when it was true. This flag only indicates that the extra Framework tick has not returned; it does not indicate that the current Dalamud UI frame should be discarded. Skipping submission hides both windows for those UI frames and skips the portrait window's visibility update.

This version removes that condition. Each `UiBuilder.Draw` submits windows according to their visibility settings. Capture still occurs before game UI commands, and Present suppression still follows the render-thread capture marker. The extra game tick, camera override, and texture source are unchanged and have not been validated in-game by this change.

Diagnostics adds **UI draws**, **During portrait submission**, and **Main-chain presents**. The second counter records how often the previous code would have skipped UI. An increasing count shows that the old condition occurs in this environment, but does not prove that Present handling or camera rendering is correct. Counters reset only when the plugin reloads.

## In-game validation

- Both windows remain visible when rendering starts or stops; dragging and resizing work.
- Captures and Suppressed presents continue increasing, usually differing by zero or one after settling. Main-chain presents also increases. These counters are not an atomic snapshot, so a momentary difference alone is not evidence of failure.
- The portrait actually displays the target character in the game. Changing Yaw or Pitch affects only the portrait view.
- If the windows are stable but the portrait is wrong, continue investigating the game camera and capture timing.

The offline WARP cube tests cover isolated D3D11 copying, cropping, camera math, and simulated scheduling. Their images are not FFXIV screenshots and cannot validate this UI change in-game.
