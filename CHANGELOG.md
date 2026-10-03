# Changelog

## 0.1.1.0

- Add optional bone stabilization, defaulting to off, with recalibration when the character model changes.
- Pause and resume capture around model redraws instead of treating an unavailable subject as a fatal rendering error.
- Keep settings tabs visible while scrolling and preserve both windows' geometry when resetting settings.
- Synchronize legacy portrait refresh with game frames and pause simulation time during the extra tick.
- Render the main view before the portrait and preserve verified CPU camera history between views.
- Reuse the main view's UI update by default, saving about 1 ms of duplicate CPU work per portrait in short live comparisons.
- Add finite, file-triggered task profiling and optional frame exports without reloading the plugin.

Legacy rendering still performs a second complete game tick and scene render. Main-view jitter, aliasing and dialogue-bubble flicker remain unresolved. The render-only prototype is investigation-only because character orientation is not correct. Offline checks and static frame exports do not establish continuous in-game visual correctness.
