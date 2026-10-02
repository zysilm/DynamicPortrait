# 0.1.0.11: Performance investigation

The user reported worsening frame rate over time. Diagnostic counters are fixed-size integers; their increasing values do not accumulate a per-frame history. Texture output uses three reusable slots. The marker registry removes consumed entries and the capture cycle permits only one outstanding portrait. None of these observations establishes the cause of the in-game slowdown.

The local saved settings at investigation time were a 60 FPS refresh limit and 4096-pixel output limit. The backend still runs a complete extra Framework tick and scene render for each portrait update, at the game's resolution. Reducing output size lowers copy/conversion/storage cost but does not lower that scene-render resolution. The extra tick also executes game logic and plugin/framework work; its cost is broader than a texture copy.

The game logs showed main-chain submission throughput falling from approximately 35 to 20 per second over part of the session, while Captures and Suppressed presents retained a constant difference of two. Later log samples returned to approximately 35 per second. This is not evidence that counter growth causes slowdown, nor proof that every possible engine/driver resource is bounded.

## Changes

- Skip formatting event-trace strings once the first 30 events have been logged. Previously, bounded logging still generated strings in the hot path. This removes unnecessary managed allocations without claiming it caused the progressive slowdown.
- Add normal-tick, portrait-tick, and capture-copy CPU wall times to diagnostics and five-second logs. These include waits and are not GPU timings; nested capture time can overlap tick time.
- Track output texture batches created, current owned textures, deferred batches, estimated texture storage, and pending capture markers. Stable dimensions normally retain six owned textures (three source copies and three opaque outputs). Resize temporarily retains old batches for four normal Presents. Estimated bytes exclude game resources, views, shaders, and driver overhead.

## Offline validation

Run the optional sustained-capture checks with:

```powershell
dotnet run --project Tests/Rendering/Rendering.csproj -c Release -- artifacts/offline-render --soak
```

8,000 steady 128 × 128 WARP captures reused three output SRVs, created no additional texture batches, and retained six owned textures using an estimated 393,216 bytes. The marker registry and capture cycle drained to zero. 100 alternating resize cycles peaked at 12 owned textures and returned to six after four Presents. Disposal also clears all owned/deferred batches. All 82 checks passed. The small synthetic capture loop took approximately 414 ms including its final GPU drain; this is not an FFXIV or hardware-GPU performance measurement.

## In-game comparison

Compare the same stationary scene with rendering stopped, portrait rendering active at the existing settings, portrait rendering at 15 FPS / 512 output, and main-view diagnostic capture. Record elapsed time, both tick times, copy time, pending markers, texture batches/storage, and graphics settings. Check whether stopping rendering immediately restores frame rate or whether plugin reload is necessary.

With stationary output dimensions, texture batches/storage and pending markers should not keep increasing. If they do, investigate that accumulation. If they remain stable while tick time grows, investigate the full extra tick, scene load, other framework work, GPU waits, and shared engine state. The actual progressive in-game cause remains unconfirmed.
