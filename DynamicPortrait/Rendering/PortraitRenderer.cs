// SPDX-License-Identifier: AGPL-3.0-or-later
// The two-tick / pre-UI capture / suppressed Present pipeline and signatures are
// adapted from WesleyLuk90/ffxiv-vr (AGPL-3.0-or-later), GameHooks.cs / VRSession.cs.
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using DynamicPortrait.Camera;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Diagnostics;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;
using GameCameraBase = FFXIVClientStructs.FFXIV.Client.Game.CameraBase;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using NativeVector = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using TickDelegate = DynamicPortrait.Rendering.NativeCallbacks.Tick;
using PresentDelegate = DynamicPortrait.Rendering.NativeCallbacks.Present;
using UiDelegate = DynamicPortrait.Rendering.NativeCallbacks.Ui;
using TargetDelegate = DynamicPortrait.Rendering.NativeCallbacks.SetTarget;

namespace DynamicPortrait.Rendering;

internal sealed unsafe class PortraitRenderer : IDisposable
{
    private delegate void MatricesDelegate(nint camera, nint ptr);
    private delegate bool VisibilityDelegate(GameCameraBase* camera, NativeObject* target, NativeVector* position, NativeVector* lookAt);

    private Hook<TickDelegate>? tick;
    private Hook<PresentDelegate>? present;
    private Hook<MatricesDelegate>? matrices;
    private Hook<UiDelegate>? ui;
    private Hook<TargetDelegate>? setTarget;
    private Hook<VisibilityDelegate>? visibility;
    private readonly IPluginLog log;
    private readonly IFramework frameworkService;
    private readonly IClientState client;
    private readonly ICondition conditions;
    private readonly IGameInteropProvider interop;
    private readonly ISigScanner scanner;
    private readonly Configuration config;
    private readonly SubjectResolver subjects;
    private readonly PortraitCamera solver = new();
    private readonly CameraOverride cameraOverride = new();
    private readonly PortraitTextures textures = new();
    private readonly RenderCommandQueue queue = new();
    private readonly CaptureCycle cycle = new();
    private long captureId;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double lastCapture = -1;
    private double lastPoseTime;
    private ulong lastSubject;
    private CameraPose pose;
    private int sourceWidth, sourceHeight, cropWidth, cropHeight;
    private volatile bool captureQueued, cameraApplied;
    private volatile bool wantsEnabled;
    private volatile bool stopping;
    private volatile bool inPortrait;
    private int callbacks;
    private long uiDraws, uiDrawsDuringPortrait, mainPresents;
    private bool disposed;
    private bool hooksReady;
    private double lastWindowVisible = -10;
    private float aspect = 1;
    private float requestedAspect = 1;
    private int resolution = 512, requestedResolution = 512;
    private double sizeChangedAt;
    private double lastDiagnostic;
    private int traceEvents;

    // Diagnostic mode is session-only and can only change while rendering is stopped.
    public bool MainViewOnly { get; private set; } = true;
    public void SetMainViewOnly(bool value) { if (!Enabled) MainViewOnly = value; }

    public bool Enabled { get; private set; }
    public long UiDraws => Interlocked.Read(ref uiDraws);
    public long UiDrawsDuringPortrait => Interlocked.Read(ref uiDrawsDuringPortrait);
    public long MainPresents => Interlocked.Read(ref mainPresents);
    public bool SubjectReady { get; private set; }
    public string Status { get; private set; } = "Rendering stopped";
    public string? Fault { get; private set; }
    public long PortraitTicks { get; private set; }
    public long NormalTicks { get; private set; }
    public long CapturedFrames => textures.Copies;
    public string PixelProbe => textures.ProbeResult;
    public double LastPortraitMilliseconds { get; private set; }
    public long SkippedPresents { get; private set; }
    public bool Ready => !stopping;
    public (nint Handle, int Width, int Height) Texture => textures.Read();

    public PortraitRenderer(Configuration config, SubjectResolver subjects, IGameInteropProvider interop,
        ISigScanner scanner, IPluginLog log, IClientState client, ICondition conditions, IFramework framework)
    {
        this.config = config;
        this.subjects = subjects;
        this.log = log;
        this.client = client;
        this.conditions = conditions;
        this.interop = interop;
        this.scanner = scanner;
        frameworkService = framework;
        // No native hooks are installed by loading the plugin or opening settings.
    }

    private void InitializeHooks()
    {
        try
        {
            // Resolve every hook before enabling any: a partially installed pipeline is unsafe.
            tick = interop.HookFromAddress<TickDelegate>((nint)NativeFramework.StaticVirtualTablePointer->Tick, Tick);
            present = interop.HookFromAddress<PresentDelegate>((nint)SwapChain.MemberFunctionPointers.Present, Present);
            matrices = interop.HookFromAddress<MatricesDelegate>(scanner.ScanText("E8 ?? ?? ?? ?? 0F 10 43 ?? C6 83"), Matrices);
            ui = interop.HookFromAddress<UiDelegate>((nint)AtkServer.MemberFunctionPointers.ProcessUICommandsAlt, BeforeUi);
            setTarget = interop.HookFromAddress<TargetDelegate>((nint)ImmediateContext.MemberFunctionPointers.DoSetTargetCommand, RenderTarget);
            visibility = interop.HookFromAddress<VisibilityDelegate>((nint)GameCameraBase.MemberFunctionPointers.ShouldDrawGameObject, ShouldDraw);
            if (Context.MemberFunctionPointers.AllocateCommand == null || Context.MemberFunctionPointers.PushBackCommand == null)
                throw new InvalidOperationException("Graphics command queue functions unresolved");
            setTarget.Enable();
            present.Enable();
            matrices.Enable();
            ui.Enable();
            visibility.Enable();
            tick.Enable();
            hooksReady = true;
            log.Info("DynamicPortrait 0.1.0.4 native pipeline initialized; diagnostic captures DXGI backbuffer before Present.");
        }
        catch (Exception e)
        {
            Fail(e);
            DisposeHooks();
        }
    }

    public void SetEnabled(bool value)
    {
        wantsEnabled = value && !stopping;
        if (!wantsEnabled) { Enabled = false; Status = "Rendering stopped"; return; }
        _ = frameworkService.RunOnFrameworkThread(() =>
        {
            if (!wantsEnabled || stopping) return;
            Fault = null;
            if (!hooksReady) InitializeHooks();
            Enabled = hooksReady;
            if (Enabled)
            {
                lastCapture = -1; solver.Reset(); Status = "Waiting for portrait window";
                traceEvents = 0;
                textures.RequestProbe();
                log.Info($"Capture started: mainViewOnly={MainViewOnly}");
            }
        });
    }

    public void InvalidateSubject()
    {
        SubjectReady = false;
        solver.Reset();
        Status = "Waiting for subject after territory change";
    }

    public void RecordUiDraw()
    {
        Interlocked.Increment(ref uiDraws);
        if (inPortrait) Interlocked.Increment(ref uiDrawsDuringPortrait);
    }

    public void MarkVisible(float windowAspect)
    {
        lastWindowVisible = clock.Elapsed.TotalSeconds;
        var value = Math.Clamp(windowAspect, 0.25f, 4);
        if (Math.Abs(value - requestedAspect) > 0.002f || config.Resolution != requestedResolution)
        {
            requestedAspect = value;
            requestedResolution = config.Resolution;
            sizeChangedAt = lastWindowVisible;
        }
        if (lastWindowVisible - sizeChangedAt >= 0.15)
        {
            aspect = requestedAspect;
            resolution = requestedResolution;
        }
    }

    private bool CanCapture()
    {
        if (!Enabled || stopping) return false;
        if (!config.ShowPortrait || clock.Elapsed.TotalSeconds - lastWindowVisible > 0.3)
        { Status = "Portrait window hidden"; return false; }
        if (!client.IsLoggedIn || client.IsGPosing || conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51]
            || conditions[ConditionFlag.OccupiedInCutSceneEvent] || conditions[ConditionFlag.WatchingCutscene78])
        {
            SubjectReady = false;
            Status = "Paused during loading, cutscenes or GPose";
            solver.Reset();
            return false;
        }
        return true;
    }

    private bool Tick(NativeFramework* native)
    {
        Interlocked.Increment(ref callbacks);
        var original = tick!.Original;
        try
        {
            if (Enabled && clock.Elapsed.TotalSeconds - lastDiagnostic >= 5)
            {
                lastDiagnostic = clock.Elapsed.TotalSeconds;
                log.Info($"Pipeline: mainViewOnly={MainViewOnly}, normal={NormalTicks}, portrait={PortraitTicks}, captures={CapturedFrames}, suppressed={SkippedPresents}, mainPresents={MainPresents}, ui={UiDraws}, status={Status}, pixels={textures.ProbeResult}");
            }
            // The diagnostic baseline must bypass both the extra tick and the
            // pre-UI marker. Its source is the final DXGI buffer in Present.
            if (!MainViewOnly && !inPortrait)
            {
                var render = false;
                try
                {
                    if (Enabled && cycle.TimedOut(clock.Elapsed.TotalSeconds))
                        Fail(new InvalidOperationException("Render-thread capture timed out. Pending commands remain registered until consumed; please report the diagnostics."));
                    if (CanCapture() && clock.Elapsed.TotalSeconds - lastCapture >= 1.0 / config.RefreshRate)
                    {
                        SubjectReady = subjects.TryRead(config, out var bone);
                        Status = subjects.Status;
                        if (SubjectReady && GameTextureSource.TrySourceSize(out sourceWidth, out sourceHeight))
                        {
                            var now = clock.Elapsed.TotalSeconds;
                            if (lastSubject != subjects.CurrentId) { solver.Reset(); lastSubject = subjects.CurrentId; }
                            pose = solver.Solve(bone, config, (float)(now - lastPoseTime));
                            lastPoseTime = now;
                            (cropWidth, cropHeight) = PortraitCamera.Crop(sourceWidth, sourceHeight, resolution, aspect);
                            render = cycle.TryBegin(now, out captureId);
                            lastCapture = now;
                        }
                    }
                }
                catch (Exception e) { Fail(e); }
                if (render)
                {
                    var started = Stopwatch.GetTimestamp();
                    cameraApplied = captureQueued = false;
                    inPortrait = true;
                    bool result;
                    try
                    {
                        PortraitTicks++;
                        TraceEvent("portrait tick enter");
                        result = original(native);
                    }
                    finally
                    {
                        cameraOverride.Restore();
                        inPortrait = false;
                        LastPortraitMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        TraceEvent("portrait tick exit");
                    }
                    // Dalamud disposes plugin-scoped hooks immediately after Plugin.Dispose.
                    // Never call the captured trampoline again if unloading happened in Original.
                    if (stopping) return result;
                    if (!captureQueued)
                    {
                        cycle.CancelBeforeSubmission(captureId);
                        Fail(new InvalidOperationException($"Portrait capture was not submitted (cameraApplied={cameraApplied}). No render-thread completion is required at tick return."));
                    }
                }
            }
            // Always submit a normal tick after the portrait tick, even if a managed capture step failed.
            NormalTicks++;
            if (Enabled && !MainViewOnly) TraceEvent("normal tick enter");
            return original(native);
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    private void Matrices(nint camera, nint ptr)
    {
        Interlocked.Increment(ref callbacks);
        try
        {
            if (inPortrait && cameraOverride.Matches(camera)) cameraOverride.Restore();
            matrices!.Original(camera, ptr);
            if (!inPortrait || stopping) return;
            try
            {
                var manager = SceneCameraManager.Instance();
                if (manager == null || manager->CameraIndex is < 0 or >= 14) return;
                var current = manager->CurrentCamera;
                if (current == null || current->RenderCamera == null || (nint)current->RenderCamera != camera) return;
                cameraOverride.Apply(current, pose, sourceWidth, sourceHeight, cropWidth, cropHeight);
                cameraApplied = true;
                TraceEvent("portrait matrices applied");
            }
            catch (Exception e) { cameraOverride.Restore(); Fail(e); }
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    private void BeforeUi(AtkServer* server, bool flag)
    {
        Interlocked.Increment(ref callbacks);
        try
        {
            if (inPortrait && cameraApplied && !captureQueued && !stopping)
            {
                try
                {
                    captureQueued = queue.EnqueueCapture(captureId, cropWidth, cropHeight);
                    TraceEvent($"submit id={captureId}, queued={captureQueued}, portrait={inPortrait}");
                }
                catch (Exception e) { Fail(e); }
            }
            ui!.Original(server, flag);
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    private void RenderTarget(ImmediateContext* context, RenderCommandSetTarget* command)
    {
        Interlocked.Increment(ref callbacks);
        try
        {
            if (command != null && queue.TryTake((nint)command, out var request))
            {
                cycle.CaptureExecuted(request.Id);
                TraceEvent($"capture id={request.Id}, cpuPortrait={inPortrait}");
                if (!stopping)
                    try { GameTextureSource.Capture(textures, request.Width, request.Height); }
                    catch (Exception e) { Fail(e); }
                // Native fallback is a real target bind. On success no rebinding is needed.
                return;
            }
            setTarget!.Original(context, command);
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    private void Present(SwapChain* swapChain)
    {
        Interlocked.Increment(ref callbacks);
        try
        {
            var device = Device.Instance();
            if (device != null && device->SwapChain == swapChain && Enabled)
            {
                TraceEvent($"present enter, cpuPortrait={inPortrait}");
                if (MainViewOnly)
                {
                    try
                    {
                        if (CanCapture() && clock.Elapsed.TotalSeconds - lastCapture >= 1.0 / config.RefreshRate)
                        {
                            var firstCapture = lastCapture < 0;
                            lastCapture = clock.Elapsed.TotalSeconds;
                            SwapChainCapture.Capture(textures, (Silk.NET.DXGI.IDXGISwapChain*)swapChain->DXGISwapChain,
                                (Silk.NET.Direct3D11.ID3D11DeviceContext*)device->D3D11DeviceContext, resolution, aspect);
                            SubjectReady = true;
                            Status = "Diagnostic: final backbuffer (includes game UI)";
                            if (firstCapture) log.Info($"Final-backbuffer first capture: {textures.ProbeResult}");
                        }
                    }
                    catch (Exception e) { Fail(e); }
                }
            }
            if (device != null && device->SwapChain == swapChain && cycle.ConsumePresent(out var suppress))
            {
                TraceEvent($"present after capture, suppress={suppress}, cpuPortrait={inPortrait}");
                if (suppress)
                {
                    SkippedPresents++;
                    return;
                }
            }
            present!.Original(swapChain);
            if (device != null && device->SwapChain == swapChain)
                Interlocked.Increment(ref mainPresents);
            if (!stopping) textures.OnPresented();
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    private bool ShouldDraw(GameCameraBase* camera, NativeObject* target, NativeVector* position, NativeVector* lookAt)
    {
        Interlocked.Increment(ref callbacks);
        try
        {
            if (inPortrait && target != null && (nint)target == subjects.CurrentAddress) return true;
            return visibility!.Original(camera, target, position, lookAt);
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    private void Fail(Exception e)
    {
        Enabled = false;
        wantsEnabled = false;
        SubjectReady = false;
        Fault = e.Message;
        Status = "Rendering stopped after an error";
        log.Error(e, "DynamicPortrait rendering failure");
    }

    private void TraceEvent(string message)
    {
        if (Interlocked.Increment(ref traceEvents) <= 30)
            log.Info($"Pipeline event t={clock.Elapsed.TotalMilliseconds:F1} thread={Environment.CurrentManagedThreadId}: {message}");
    }

    private void DisposeHooks()
    {
        tick?.Dispose(); tick = null;
        matrices?.Dispose(); matrices = null;
        ui?.Dispose(); ui = null;
        visibility?.Dispose(); visibility = null;
        present?.Dispose(); present = null;
        setTarget?.Dispose(); setTarget = null;
        hooksReady = false;
    }

    public void Dispose()
    {
        if (disposed || stopping) return;
        stopping = true;
        Enabled = false;
        cameraOverride.Restore();
        DisposeHooks();
        queue.Clear();
        // Hooks must be disposed synchronously: Dalamud cleans up plugin-scoped hooks on
        // unload. Only GPU resources are deferred, to let outstanding ImGui draws finish.
        _ = frameworkService.RunOnTick(FinishDispose, delayTicks: 2);
    }

    private void FinishDispose()
    {
        if (Volatile.Read(ref callbacks) != 0)
        {
            _ = frameworkService.RunOnTick(FinishDispose, delayTicks: 2);
            return;
        }
        textures.Dispose();
        disposed = true;
    }
}
