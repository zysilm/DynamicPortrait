// SPDX-License-Identifier: AGPL-3.0-or-later
// Offline: game structs are used only for metadata and stack-allocated test fixtures.
// No FFXIV singleton, game member function, hook or game process is accessed.
using DynamicPortrait;
using DynamicPortrait.Camera;
using DynamicPortrait.Rendering;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Numerics;
using System.Reflection;
using Silk.NET.Direct3D11;
using Task = System.Threading.Tasks.Task;

internal static unsafe class Program
{
    private static int passed;
    public static void Check(string name, bool condition)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS {name}");
        passed++;
    }

    public static void Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/offline-render");
        Directory.CreateDirectory(output);
        CheckAbi(typeof(NativeCallbacks.Present), typeof(SwapChain), "Present");
        CheckAbi(typeof(NativeCallbacks.SetTarget), typeof(ImmediateContext), "DoSetTargetCommand");
        CheckAbi(typeof(NativeCallbacks.Tick), typeof(Framework), "Tick");
        CheckAbi(typeof(NativeCallbacks.Ui), typeof(AtkServer), "ProcessUICommandsAlt");
        TestDelayedCommands();
        TestSubjectRedraw();
        TestFallback();
        CameraStateTest.Run();

        using var gpu = new OffscreenGpu();
        SwapChainTest.Run(gpu);
        TestBoneLock(gpu, output);
        using var captured = new PortraitTextures();
        var bone = new BonePose(Vector3.Zero, Quaternion.Identity, 0);
        var camera = new PortraitCamera();
        var settings = new Configuration { Offset = Vector3.Zero, Distance = 2, FieldOfView = 40, Pitch = 20, Yaw = 35, Smoothing = 0 };
        var mainCamera = camera.Solve(bone, settings, 0.1f);
        var mainProjection = PortraitCamera.View(mainCamera) * PortraitCamera.Projection(mainCamera, 640, 480, 640, 480);
        gpu.Draw(mainProjection);
        var main = gpu.Read(gpu.Scene);
        Png.Write(Path.Combine(output, "main-view.png"), 640, 480, main);
        Check("Real D3D11 rasterization produces geometry", HasGeometry(main));

        settings.Pitch = settings.Yaw = 0;
        settings.FieldOfView = 30;
        var portraitCamera = camera.Solve(bone, settings, 0.1f);
        var portraitProjection = PortraitCamera.View(portraitCamera) * PortraitCamera.Projection(portraitCamera, 640, 480, 256, 256);
        gpu.Draw(portraitProjection);
        var source = gpu.Read(gpu.Scene);
        Check("Two camera poses render different views", !main.AsSpan().SequenceEqual(source));
        captured.Capture(gpu.Scene, gpu.Context, 256, 256);
        var front = captured.Read();
        var crop = gpu.ReadView(front.Handle);
        Check("Captured SRV dimensions", front.Width == 256 && front.Height == 256 && front.Handle != 0);
        Check("Every captured pixel equals the corresponding source pixel", CropEquals(source, 640, 480, crop, 256, 256));
        Check("Capturing leaves source texture unchanged", source.AsSpan().SequenceEqual(gpu.Read(gpu.Scene)));
        Png.Write(Path.Combine(output, "portrait-view.png"), 256, 256, crop);

        gpu.Draw(mainProjection);
        Check("Later main-view rendering cannot overwrite captured portrait", crop.AsSpan().SequenceEqual(gpu.ReadView(front.Handle)));
        Check("Main-view redraw is unchanged after capture", main.AsSpan().SequenceEqual(gpu.Read(gpu.Scene)));
        captured.Capture(gpu.Scene, gpu.Context, 256, 256);
        Check("Next capture uses a different buffer", captured.Read().Handle != front.Handle);
        Check("Previous frame's SRV remains readable", crop.AsSpan().SequenceEqual(gpu.ReadView(front.Handle)));
        captured.Capture(gpu.Scene, gpu.Context, 256, 256);
        // The next capture wraps to the first texture while it is still bound for UI.
        var previousView = (ID3D11ShaderResourceView*)front.Handle;
        gpu.Context->PSSetShaderResources(0, 1, &previousView);
        gpu.Context->CSSetShaderResources(5, 1, &previousView);
        captured.Capture(gpu.Scene, gpu.Context, 256, 256);
        ID3D11ShaderResourceView* restored = null;
        gpu.Context->PSGetShaderResources(0, 1, &restored);
        Check("Opaque pass restores PS resource after UAV alias unbinding", restored == previousView);
        if (restored != null) restored->Release();
        gpu.Context->CSGetShaderResources(5, 1, &restored);
        Check("Opaque pass restores other CS resource slots", restored == previousView);
        if (restored != null) restored->Release();
        ID3D11ShaderResourceView* empty = null;
        gpu.Context->PSSetShaderResources(0, 1, &empty);
        gpu.Context->CSSetShaderResources(5, 1, &empty);
        Check("Triple-buffer wraparound stays valid", CropEquals(main, 640, 480, gpu.ReadView(captured.Read().Handle), 256, 256));

        foreach (var (width, height) in new[] { (128, 256), (320, 180), (640, 480), (256, 256) })
        {
            captured.Capture(gpu.Scene, gpu.Context, width, height);
            Check($"Resize {width}x{height} preserves exact crop", CropEquals(main, 640, 480, gpu.ReadView(captured.Read().Handle), width, height));
            for (var i = 0; i < 5; i++) captured.OnPresented();
        }
        var rejected = false;
        try { captured.Capture(gpu.Scene, gpu.Context, 1000, 1000); }
        catch (InvalidOperationException) { rejected = true; }
        Check("Oversized capture is rejected before issuing GPU copy", rejected);
        gpu.ClearTransparent();
        captured.RequestProbe();
        var transparent = gpu.Read(gpu.Scene);
        captured.Capture(gpu.Scene, gpu.Context, 256, 256);
        var visible = gpu.ReadView(captured.Read().Handle);
        Check("Zero-alpha scene becomes visible without changing RGB", visible[0] == transparent[0]
            && visible[1] == transparent[1] && visible[2] == transparent[2]
            && Enumerable.Range(0, visible.Length / 4).All(i => visible[i * 4 + 3] == 255));
        Check("Opaque conversion leaves game scene alpha unchanged", gpu.Read(gpu.Scene).AsSpan().SequenceEqual(transparent));
        Check("Runtime pixel probe detects transparent scene", captured.ProbeResult.Contains("alpha=0.000..0.000"));
        captured.Dispose();
        Check("Disposal clears published texture", captured.Read().Handle == 0);
        Check("Disposal releases all owned texture batches", captured.Statistics.LiveTextures == 0
            && captured.Statistics.RetiredSets == 0 && captured.Statistics.TextureBytes == 0);
        if (args.Contains("--soak")) TestCaptureSoak(gpu, output);
        gpu.AssertNoDeviceError();
        File.WriteAllText(Path.Combine(output, "result.txt"), $"PASS: {passed} checks. Backend: D3D11 WARP. Game process not used.\n");
        Console.WriteLine($"{passed} offline ABI, scheduling and D3D11 checks passed. Images: {output}");
    }

    private static void CheckAbi(Type ours, Type gameType, string name)
    {
        var delegates = gameType.GetNestedType("Delegates", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Exception($"No generated delegates for {gameType.Name}");
        var canonical = delegates.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Exception($"No generated delegate {gameType.Name}.{name}");
        var a = ours.GetMethod("Invoke")!;
        var b = canonical.GetMethod("Invoke")!;
        Check($"ABI {gameType.Name}.{name} exactly matches host metadata", a.ReturnType == b.ReturnType
            && a.GetParameters().Select(p => p.ParameterType).SequenceEqual(b.GetParameters().Select(p => p.ParameterType)));
    }

    private static void TestCaptureSoak(OffscreenGpu gpu, string output)
    {
        using var textures = new PortraitTextures();
        var registry = new RenderCommandQueue();
        var cycle = new CaptureCycle();
        for (var i = 0; i < 500; i++)
        {
            textures.Capture(gpu.Scene, gpu.Context, 128, 128);
            textures.OnPresented();
        }
        // Drain GPU work before measuring. This benchmark is a synthetic WARP
        // capture loop, not a measurement of the game's renderer or real VRAM.
        _ = gpu.ReadView(textures.Read().Handle);
        var warmStats = textures.Statistics;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var handles = new HashSet<nint>();
        const int captures = 8000;
        for (var i = 0; i < captures; i++)
        {
            textures.Capture(gpu.Scene, gpu.Context, 128, 128);
            textures.OnPresented();
            handles.Add(textures.Read().Handle);
            if (!cycle.TryBegin(i, out var id) || !registry.Register(123, new(id, 128, 128))
                || !registry.TryTake(123, out var request)) throw new Exception("Soak marker registration failed");
            cycle.CaptureExecuted(request.Id);
            if (!cycle.ConsumePortraitPresent()) throw new Exception("Soak marker completion failed");
        }
        _ = gpu.ReadView(textures.Read().Handle);
        timer.Stop();
        var managedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var finalStats = textures.Statistics;
        Check("8000 steady captures reuse exactly three output SRVs", handles.Count == 3);
        Check("8000 captures do not allocate additional texture sets", finalStats.CreatedSets == warmStats.CreatedSets
            && finalStats.LiveTextures == 6 && finalStats.RetiredSets == 0 && finalStats.TextureBytes == warmStats.TextureBytes);
        Check("8000 completed markers leave no registry or cycle backlog", registry.PendingCount == 0 && !cycle.HasPending);
        var peakTextures = 0;
        for (var resize = 0; resize < 100; resize++)
        {
            var size = resize % 2 == 0 ? 192 : 128;
            textures.Capture(gpu.Scene, gpu.Context, size, size);
            peakTextures = Math.Max(peakTextures, textures.Statistics.LiveTextures);
            for (var frame = 0; frame < 4; frame++) textures.OnPresented();
        }
        _ = gpu.ReadView(textures.Read().Handle);
        Check("100 resize cycles retire old textures within four Presents", peakTextures == 12
            && textures.Statistics.LiveTextures == 6 && textures.Statistics.RetiredSets == 0);
        var report = $"Synthetic D3D11 WARP; no game process.\nCaptures: {captures}\nElapsed ms (includes final GPU drain): {timer.Elapsed.TotalMilliseconds:F2}\n"
            + $"Managed bytes on test thread (includes marker registry and readback): {managedBytes}\n"
            + $"Steady texture sets created: {finalStats.CreatedSets}\nSteady owned textures: {finalStats.LiveTextures}\n"
            + $"Steady estimated texture storage bytes: {finalStats.TextureBytes}\nResize peak owned textures: {peakTextures}\n";
        File.WriteAllText(Path.Combine(output, "capture-soak.txt"), report);
        Console.WriteLine(report);
    }

    private static void TestBoneLock(OffscreenGpu gpu, string output)
    {
        var camera = new PortraitCamera();
        var settings = new Configuration { LockBone = true, Distance = 1.5f, Pitch = -5.6f, Yaw = 15, Roll = 5, FieldOfView = 45, Smoothing = 2 };
        using var captured = new PortraitTextures();
        byte[]? reference = null;
        foreach (var (position, rotation) in new[]
        {
            (Vector3.Zero, Quaternion.Identity),
            (new Vector3(2, 4, -1), Quaternion.CreateFromYawPitchRoll(0.9f, 0.35f, -0.2f)),
            (new Vector3(-3, 0.2f, 2), Quaternion.CreateFromYawPitchRoll(-1.8f, -0.7f, 0.8f)),
        })
        {
            var bone = new BonePose(position, rotation, -1);
            var solved = camera.Solve(bone, settings, 1f / 60);
            // The cube represents a rigid head attached to the animated bone.
            // Transform it into world space, then use the actual camera solver
            // and GPU capture code. Smoothing would make the head drift here.
            var world = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
            gpu.Draw(world * PortraitCamera.View(solved) * PortraitCamera.Projection(solved, 640, 480, 256, 256));
            captured.Capture(gpu.Scene, gpu.Context, 256, 256);
            var pixels = gpu.ReadView(captured.Read().Handle);
            Check($"Locked animated head rasterizes visible geometry at {position}", HasGeometry(pixels));
            if (reference == null)
            {
                reference = pixels;
                Png.Write(Path.Combine(output, "bone-lock-neutral.png"), 256, 256, pixels);
            }
            else
            {
                var changed = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                    if (Math.Abs(pixels[i] - reference[i]) > 2 || Math.Abs(pixels[i + 1] - reference[i + 1]) > 2
                        || Math.Abs(pixels[i + 2] - reference[i + 2]) > 2) changed++;
                // Allow a few edge pixels for floating-point rasterization.
                Check($"Animated bone translation/rotation preserves GPU portrait at {position}", changed <= 32);
                Png.Write(Path.Combine(output, $"bone-lock-moved-{position.X:F0}.png"), 256, 256, pixels);
            }
        }
    }

    private static void TestDelayedCommands()
    {
        var cycle = new CaptureCycle();
        Check("Begin portrait cycle", cycle.TryBegin(0, out var first));
        Check("CPU tick return does not imply render completion", !cycle.ConsumePortraitPresent());
        Check("Outstanding frame prevents a second submission", !cycle.TryBegin(0.01, out _));
        Task.Run(() => { Thread.Sleep(30); cycle.CaptureExecuted(first); }).GetAwaiter().GetResult();
        Check("Delayed render-thread completion suppresses exactly one Present", cycle.ConsumePortraitPresent() && !cycle.ConsumePortraitPresent());
        Check("Next frame starts after completion", cycle.TryBegin(1, out var second));
        cycle.CaptureExecuted(first);
        Check("Old frame cannot suppress a normal Present", !cycle.ConsumePortraitPresent());
        Check("Timeout is observable", cycle.TimedOut(5));
        Check("Timeout does not discard the pending frame", !cycle.TryBegin(5, out _));
        cycle.CaptureExecuted(second);
        Check("Timed-out frame can still drain safely", cycle.ConsumePortraitPresent());
        cycle.TryBegin(6, out var aborted);
        cycle.CancelBeforeSubmission(aborted);
        Check("Unsubmitted CPU frame can be cancelled", cycle.TryBegin(7, out _));
        var diagnostic = new CaptureCycle();
        diagnostic.TryBegin(0, out var mainView, suppress: false);
        diagnostic.CaptureExecuted(mainView);
        Check("Main-view diagnostic completes without suppressing Present", diagnostic.ConsumePresent(out var suppress) && !suppress);
        Check("Main-view diagnostic permits next capture", diagnostic.TryBegin(1, out _, suppress: false));
        var matched = new CaptureCycle();
        matched.TryBegin(0, out var submitted);
        Check("Portrait submission retains its frame until completion", matched.HasPending && !matched.ConsumePresent(out _, out _));
        matched.CaptureExecuted(submitted);
        Check("Completed Present retains the exact submitted portrait ID", matched.ConsumePresent(out var completed, out suppress)
            && completed == submitted && suppress && !matched.HasPending);
    }

    private static void TestFallback()
    {
        var target = new Texture { ActualWidth = 640, ActualHeight = 480, Width3 = 640, Height3 = 480 };
        var command = RenderCommandQueue.CreateFallback(&target);
        var queue = new RenderCommandQueue();
        var address = (nint)(&command);
        Check("Capture marker always has a real first target", command.RenderTargetCount == 1 && command.RenderTargets[0].Value == &target);
        Check("Register marker", queue.Register(address, new RenderCommandQueue.CaptureRequest(1, 256, 256)));
        var output = Task.Run(() => { Thread.Sleep(30); return queue.TryTake(address, out var request) && request.Id == 1; }).GetAwaiter().GetResult();
        Check("Marker survives asynchronous consumption", output && !queue.TryTake(address, out _));
        queue.Register(address, new RenderCommandQueue.CaptureRequest(2, 256, 256));
        queue.Clear(); // Simulate unloading before the native command is executed.
        Check("Missing registry entry leaves native command valid", !queue.TryTake(address, out _) && command.RenderTargetCount == 1
            && command.RenderTargets[0].Value->Height3 == 480); // Exact null+0x4C access from crash dump.
        var refusedNull = false;
        try { RenderCommandQueue.CreateFallback(null); }
        catch (ArgumentNullException) { refusedNull = true; }
        Check("Null target cannot become a queued marker", refusedNull);
    }

    private static void TestSubjectRedraw()
    {
        var subject = new SubjectIdentity(42, 100, 200, 300, 400, 500);
        var replacement = subject with { DrawObject = 201, Skeleton = 301, Pose = 401, Havok = 501 };
        var frame = new PortraitSubjectFrame();
        var cycle = new CaptureCycle();
        cycle.TryBegin(0, out var id);
        frame.Begin(subject);
        Check("Ready subject can submit portrait matrices", frame.Accept(true, subject));
        Check("Temporary missing model pauses this submission", !frame.Accept(false, default) && frame.Unavailable);
        Check("Same-frame recovery cannot reuse an invalidated pose", !frame.Accept(true, subject));
        cycle.CancelBeforeSubmission(id);
        Check("Redraw before camera submission cancels without suppressing main Present", !cycle.HasPending && !cycle.ConsumePortraitPresent());
        Check("Next portrait can retry without restarting rendering", cycle.TryBegin(1, out id));
        frame.Begin(replacement);
        Check("New model generation resumes on the next frame", frame.Accept(true, replacement));
        cycle.CaptureExecuted(id);
        Check("Resumed portrait drains through the matching Present", cycle.ConsumePresent(out var completed, out var suppress) && completed == id && suppress);

        cycle.TryBegin(2, out id);
        frame.Begin(subject);
        foreach (var changed in new[]
        {
            subject with { DrawObject = 201 }, subject with { Skeleton = 301 },
            subject with { Pose = 401 }, subject with { Havok = 501 },
            subject with { Id = 43 }, subject with { Address = 101 },
        })
        {
            frame.Begin(subject);
            Check($"Replacement identity {changed} cannot use the previous model snapshot", !frame.Accept(true, changed));
        }
        // A marker already in the render queue must survive invalidation. It
        // drains once, suppresses that view, and cannot suppress the next frame.
        cycle.CaptureExecuted(id);
        Check("Redraw after submission preserves exactly one draining Present", frame.Unavailable
            && cycle.ConsumePresent(out completed, out suppress) && completed == id && suppress && !cycle.ConsumePortraitPresent());
        Check("Already-submitted redraw does not block recovery", cycle.TryBegin(3, out _));
    }

    private static bool HasGeometry(byte[] rgba)
    {
        var differing = 0;
        for (var i = 4; i < rgba.Length; i += 4)
            if (rgba[i] != rgba[0] || rgba[i + 1] != rgba[1] || rgba[i + 2] != rgba[2]) differing++;
        return differing > 1000;
    }

    private static bool CropEquals(byte[] source, int sw, int sh, byte[] crop, int cw, int ch)
    {
        if (crop.Length != cw * ch * 4) return false;
        for (var y = 0; y < ch; y++)
            if (!source.AsSpan((((sh - ch) / 2 + y) * sw + (sw - cw) / 2) * 4, cw * 4)
                .SequenceEqual(crop.AsSpan(y * cw * 4, cw * 4))) return false;
        return true;
    }
}
