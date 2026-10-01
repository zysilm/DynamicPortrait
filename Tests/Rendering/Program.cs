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
        TestFallback();

        using var gpu = new OffscreenGpu();
        SwapChainTest.Run(gpu);
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
