# 0.1.0.3: Opaque output and in-game diagnostics

The user reported that enabling 0.1.0.2 left only a translucent GUI background in the portrait window, while counters increased and parts of the main view's text and 3D scene flickered. This is an in-game failure that the previous offline tests did not cover.

## Changes

- Previously, scene alpha was passed directly to ImGui. Scene alpha does not necessarily represent opacity. The plugin now copies into a private texture and uses a compute shader to preserve RGB while setting alpha to one. Output is RGBA8; FP16 values outside the display range are clipped, with no HDR tone mapping yet.
- The compute pass restores the original compute shader, class instances, UAV slot zero, and SRV bindings in every shader stage. This includes old portrait SRVs automatically unbound by D3D11 because of read/write conflicts during buffer rotation.
- Each explicit start reads back the captured texture once and logs its sampled alpha range and maximum RGB. This readback can briefly stall the GPU; it exports no screenshot or character information.
- The first 30 submission, capture, and Present events log their time and thread. Runtime counters are summarized every five seconds.
- This version defaults to **Diagnostic: capture main view only**. It captures the current game camera without changing it, adding a tick, or suppressing Present. This session option is not saved. Stop rendering before changing it; clearing it selects the experimental second camera.

## Validation limits

Offline D3D11 WARP regression checks cover zero-alpha conversion to opaque output, RGB preservation, an unchanged source texture, binding restoration, and diagnostic capture without Present suppression. Pixel logs are still needed to determine the cause of the in-game transparency. Main-view flicker is not yet resolved; shared engine state and second-camera timing still need validation.

## In-game steps

Reload 0.1.0.3, keep diagnostic mode selected, enable rendering for approximately five seconds, and stop it. The expected result is a center crop of the main view. Portrait ticks and Suppressed presents remain zero; Captures and Main-chain presents increase. If the image is missing or the main view flickers, investigate logs before attributing the problem to the second camera or marking the test successful.

Only compare second-camera behavior after main-view copying is stable. A working main-view copy does not prove that the second camera works. Offline test images are not game screenshots.
