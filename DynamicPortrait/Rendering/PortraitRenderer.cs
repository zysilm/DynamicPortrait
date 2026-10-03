// SPDX-License-Identifier: AGPL-3.0-or-later
// The two-tick / render marker / suppressed Present pipeline and signatures are
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
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;
using CameraUpdateDelegate = DynamicPortrait.Rendering.NativeCallbacks.CameraUpdate;
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

    private Hook<CameraUpdateDelegate>? gameplayCameraUpdate;
    private Hook<NativeCallbacks.Ui3DUpdate>? ui3DUpdate;
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate void UiUpdateDelegate(nint module, float delta);
    private Hook<UiUpdateDelegate>? uiUpdate;
    public bool ReuseMainUiUpdate { get; set; } = true;
    internal bool? InvestigationReuseMainUiUpdate;
    public long UiUpdates { get; private set; }
    public long SuppressedUiUpdates { get; private set; }
    public long SuppressedUi3DUpdates { get; private set; }
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
    private readonly PortraitSubjectFrame subjectFrame = new();
    private long captureId;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double lastCapture = -1;
    private double lastPoseTime;
    private SubjectIdentity lastSubject;
    private string lastBone = "";
    private OrientationMode lastOrientation;
    private bool lastLockBone;
    private bool poseReady;
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
    private bool normalAfterPortrait;
    private bool suppressRenderOnlyPresent;
    internal RenderInvestigation? Investigation;
    internal bool InvestigationCapture;
    internal RenderBackend? InvestigationBackend;
    internal int InvestigationRefreshLimit = 60;
    private RenderOnlyBatch? renderOnly;
    private readonly Action applyRenderOnlyCamera, queueRenderOnlyMarker;
    public long RenderOnlySubmissions { get; private set; }
    internal bool IsPortraitTick => inPortrait;
    internal nint InvestigationActor => subjects.LocalPlayerAddress;
    internal bool InvestigationReplayGameplayCamera;
    internal bool InvestigationSpatialAntialiasing;
    public bool SpatialAntialiasingTest { get; private set; }
    public void SetSpatialAntialiasingTest(bool value)
    {
        if (Enabled || wantsEnabled) return;
        SpatialAntialiasingTest = value;
        log.Info($"Spatial antialiasing experiment: {value}. Native settings are scoped to rendering ticks; saved game settings are unchanged.");
    }
    public long GameplayCameraUpdates { get; private set; }
    public long SuppressedGameplayCameraUpdates { get; private set; }
    public RenderBackend Backend => InvestigationBackend ?? config.Backend;

    internal void PrepareInvestigation()
    {
        SetEnabled(false);
        if (!hooksReady) InitializeHooks();
        if (!hooksReady) throw new InvalidOperationException(Fault ?? "Investigation hooks unavailable");
    }

    // Diagnostic mode is session-only and can only change while rendering is stopped.
    public bool MainViewOnly { get; private set; }
    public void SetMainViewOnly(bool value)
    {
        if (Enabled || wantsEnabled) return;
        MainViewOnly = value;
        SubjectReady = false;
        textures.ClearPublished();
    }

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
    public double LastNormalMilliseconds { get; private set; }
    public double LastCopyMilliseconds { get; private set; }
    public PortraitTextures.ResourceStats TextureStatistics => textures.Statistics;
    public int PendingMarkers => queue.PendingCount;
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
        applyRenderOnlyCamera = ApplyRenderOnlyCamera;
        queueRenderOnlyMarker = QueueRenderOnlyMarker;
        resolution = requestedResolution = config.Resolution;
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
            var gameCameras = GameCameraManager.Instance();
            if (gameCameras == null || gameCameras->Camera == null)
                throw new InvalidOperationException("Main gameplay camera is unavailable");
            var gameCameraTable = *(nint**)gameCameras->Camera;
            gameplayCameraUpdate = interop.HookFromAddress<CameraUpdateDelegate>(gameCameraTable[3], GameplayCameraUpdate);
            ui3DUpdate = interop.HookFromAddress<NativeCallbacks.Ui3DUpdate>(scanner.ScanText(
                "53 56 57 48 83 EC 30 33 F6 48 89 6C 24 50 4C 89 74 24 60 48 8B F9"), UpdateUi3D);
            uiUpdate = interop.HookFromAddress<UiUpdateDelegate>(scanner.ScanText(
                "48 8B C4 41 56 48 83 EC 60 FF 81 D4 08 00 00 4C 8B F1 48 89 58 08 48 81 C1 E0 08 00 00"), UpdateUi);
            // History offsets are verified for this native camera implementation.
            var sceneCameras = SceneCameraManager.Instance();
            var constantsAddress = scanner.ScanText(
                "40 55 57 48 8D AC 24 68 FE FF FF 48 81 EC 98 02 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 D0 00 00 00 48 8B 01 48 8B F9");
            if (sceneCameras == null || sceneCameras->CurrentCamera == null
                || sceneCameras->CurrentCamera->RenderCamera == null
                || (*(nint**)sceneCameras->CurrentCamera->RenderCamera)[6] != constantsAddress)
                throw new InvalidOperationException($"Render camera history layout is not supported by this client (expected=0x{constantsAddress:X}, actual=0x{(sceneCameras == null || sceneCameras->CurrentCamera == null || sceneCameras->CurrentCamera->RenderCamera == null ? 0 : (*(nint**)sceneCameras->CurrentCamera->RenderCamera)[6]):X})");
            gameplayCameraUpdate.Enable();
            ui3DUpdate.Enable();
            uiUpdate.Enable();
            setTarget.Enable();
            present.Enable();
            matrices.Enable();
            ui.Enable();
            visibility.Enable();
            tick.Enable();
            hooksReady = true;
            log.Info("DynamicPortrait native pipeline initialized; portrait completion captures the final backbuffer before suppressed Present.");
        }
        catch (Exception e)
        {
            Fail(e);
            DisposeHooks();
        }
    }

    public void SetEnabled(bool value)
    {
        if (value && Investigation?.IsBaseline == true) return;
        wantsEnabled = value && !stopping;
        if (!wantsEnabled) { Enabled = false; Status = "Rendering stopped"; return; }
        _ = frameworkService.RunOnFrameworkThread(() =>
        {
            if (!wantsEnabled || stopping) return;
            Fault = null;
            if (Backend == RenderBackend.RenderOnly && InvestigationBackend == null)
            {
                Enabled = false;
                Status = "Render only is investigation-only: character view matrices are not yet correct. Select Full tick (legacy).";
                return;
            }
            if (!hooksReady) InitializeHooks();
            if (hooksReady && Backend == RenderBackend.RenderOnly)
            {
                try { renderOnly ??= new RenderOnlyBatch(scanner); }
                catch (Exception e) { Fail(e); return; }
            }
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

    public void ResetSession()
    {
        SetEnabled(false);
        _ = frameworkService.RunOnFrameworkThread(() =>
        {
            if (stopping) return;
            // Pending submitted frames still drain through their marker/Present.
            MainViewOnly = false;
            SubjectReady = false;
            Fault = null;
            solver.Reset();
            lastSubject = default;
            lastCapture = -1;
            lastPoseTime = 0;
            resolution = requestedResolution = config.Resolution;
            aspect = requestedAspect = 1;
            textures.ClearPublished();
            Status = "All settings reset; rendering stopped";
        });
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
        if (!InvestigationCapture && (!config.ShowPortrait || clock.Elapsed.TotalSeconds - lastWindowVisible > 0.3))
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
        Investigation?.BeginFrame(NormalTicks + 1);
        using var measurement = Investigation?.Measure(InvestigationSamples.Stage.Tick) ?? default;
        return TickCore(native);
    }

    private bool TickCore(NativeFramework* native)
    {
        Interlocked.Increment(ref callbacks);
        var original = tick!.Original;
        try
        {
            LogPipeline();
            if (Backend == RenderBackend.RenderOnly && !MainViewOnly)
            {
                // Native Present displays the preceding completed kernel batch.
                // The extra batch displays the previous main frame; the normal
                // tick skips presentation of the portrait.
                if (!stopping) SubmitRenderOnly();
                if (stopping) return true;
                NormalTicks++;
                var started = Stopwatch.GetTimestamp();
                bool result;
                try { result = original(native); }
                finally { LastNormalMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
                return result;
            }
            // Run gameplay and the displayed view first. Native Present handles
            // the preceding batch, so a portrait submitted at the end of the
            // previous outer tick is captured and suppressed during this tick.
            NormalTicks++;
            normalAfterPortrait = cycle.HasPending;
            TraceEvent("normal tick before portrait enter");
            Investigation?.RecordMainCameraCarryover(cameraOverride.IsActive);
            var normalStarted = Stopwatch.GetTimestamp();
            bool normalResult;
            try
            {
                using var antialiasing = new SpatialAntialiasingScope(
                    FFXIVClientStructs.FFXIV.Client.Graphics.Render.GraphicsConfig.Instance(),
                    Enabled && !MainViewOnly && (SpatialAntialiasingTest || InvestigationSpatialAntialiasing));
                normalResult = original(native);
            }
            finally
            {
                LastNormalMilliseconds = Stopwatch.GetElapsedTime(normalStarted).TotalMilliseconds;
                normalAfterPortrait = false;
            }
            // The original can unload the plugin and dispose its trampoline.
            if (stopping) return normalResult;
            // The diagnostic baseline must bypass both the extra tick and the
            // pre-UI marker. Its source is the final DXGI buffer in Present.
            if (!MainViewOnly && !inPortrait)
            {
                var render = false;
                try
                {
                    if (Enabled && cycle.TimedOut(clock.Elapsed.TotalSeconds))
                        Fail(new InvalidOperationException("Render-thread capture timed out. Pending commands remain registered until consumed; please report the diagnostics."));
                    if (!cycle.HasPending && CanCapture() && CaptureDue())
                    {
                        SubjectReady = subjects.TryRead(config, out _);
                        Status = subjects.Status;
                        if (!SubjectReady) PauseForSubject(subjects.Status);
                        if (SubjectReady && GameTextureSource.TryBackbufferSize(out sourceWidth, out sourceHeight))
                        {
                            var now = clock.Elapsed.TotalSeconds;
                            subjectFrame.Begin(subjects.CurrentIdentity);
                            (cropWidth, cropHeight) = PortraitCamera.Crop(sourceWidth, sourceHeight, resolution, aspect);
                            render = cycle.TryBegin(now, out var nextId);
                            if (render) { captureId = nextId; lastCapture = now; }
                        }
                    }
                }
                catch (Exception e) { Fail(e); }
                if (render)
                {
                    var started = Stopwatch.GetTimestamp();
                    cameraApplied = captureQueued = poseReady = false;
                    inPortrait = true;
                    bool result;
                    try
                    {
                        PortraitTicks++;
                        TraceEvent("portrait tick enter");
                        using var timing = new LegacyPortraitTiming(native);
                        using var antialiasing = new SpatialAntialiasingScope(
                            FFXIVClientStructs.FFXIV.Client.Graphics.Render.GraphicsConfig.Instance(),
                            SpatialAntialiasingTest || InvestigationSpatialAntialiasing);
                        result = original(native);
                    }
                    finally
                    {
                        // The renderer may still be using this camera after CPU
                        // submission returns. Restore at completion or before
                        // the next native SetMatrices, rather than tick return.
                        inPortrait = false;
                        LastPortraitMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        TraceEvent("portrait tick exit");
                    }
                    // Dalamud disposes plugin-scoped hooks immediately after Plugin.Dispose.
                    // Never call the captured trampoline again if unloading happened in Original.
                    if (stopping) return normalResult;
                    if (!captureQueued)
                    {
                        // Redraw can also remove the camera callback entirely.
                        // Recheck the model before treating a missing marker as
                        // a pipeline fault, while keeping real queue errors fatal.
                        if (Enabled && !subjectFrame.Unavailable)
                        {
                            var ready = subjects.TryRead(config, out _);
                            if (!subjectFrame.Accept(ready, subjects.CurrentIdentity))
                                PauseForSubject(ready ? "Character model changed before capture submission" : subjects.Status);
                        }
                        if (cameraApplied) cycle.CaptureExecuted(captureId);
                        else cycle.CancelBeforeSubmission(captureId);
                        cameraOverride.Restore();
                        if (!subjectFrame.Unavailable && Enabled && Fault == null)
                            Fail(new InvalidOperationException($"Portrait capture was not submitted (cameraApplied={cameraApplied}). No render-thread completion is required at tick return."));
                    }
                }
            }
            return normalResult;
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    private bool SuppressGameplayCamera(GameCameraBase* camera)
    {
        if (!inPortrait || Backend != RenderBackend.FullTick || stopping || InvestigationReplayGameplayCamera) return false;
        var manager = GameCameraManager.Instance();
        return manager != null && (nint)camera == (nint)manager->Camera;
    }

    private void GameplayCameraUpdate(GameCameraBase* camera)
    {
        Interlocked.Increment(ref callbacks);
        var started = Stopwatch.GetTimestamp();
        var portrait = inPortrait;
        var suppressed = false;
        try
        {
            suppressed = SuppressGameplayCamera(camera);
            if (suppressed)
            {
                SuppressedGameplayCameraUpdates++;
                // The later scene-camera update still submits portrait matrices.
                // Avoid driving gameplay follow history and bone orbit hooks a
                // second time; native input/state sampling remains unchanged.
                return;
            }
            GameplayCameraUpdates++;
            gameplayCameraUpdate!.Original(camera);
        }
        finally
        {
            Investigation?.RecordGameplayCameraUpdate(portrait, suppressed, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Interlocked.Decrement(ref callbacks);
        }
    }

    private void Matrices(nint camera, nint ptr)
    {
        using var measurement = Investigation?.Measure(InvestigationSamples.Stage.Matrices) ?? default;
        if (Investigation != null)
        {
            var cameras = SceneCameraManager.Instance();
            if (cameras != null && cameras->CurrentCamera != null
                && (nint)cameras->CurrentCamera->RenderCamera == camera)
                Investigation.RecordBoneState("MainMatrices", InvestigationActor);
        }
        Interlocked.Increment(ref callbacks);
        try
        {
            if (cameraOverride.Matches(camera))
            {
                var observe = Investigation != null && ptr != 0;
                var incoming = observe ? *(System.Numerics.Matrix4x4*)ptr : default;
                var aliasesScene = observe && cameraOverride.IsSceneView(ptr);
                cameraOverride.Restore();
                if (observe) Investigation?.RecordMatrixRestore(camera, inPortrait, aliasesScene, incoming, *(System.Numerics.Matrix4x4*)ptr);
            }
            matrices!.Original(camera, ptr);
            if (!inPortrait || stopping || !Enabled || subjectFrame.Unavailable) return;
            ApplyPortraitCamera(camera);
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    private void ApplyPortraitCamera(nint camera)
    {
        try
        {
            var manager = SceneCameraManager.Instance();
            if (manager == null || manager->CameraIndex is < 0 or >= 14) return;
            var current = manager->CurrentCamera;
            if (current == null || current->RenderCamera == null || (nint)current->RenderCamera != camera) return;
            var ready = subjects.TryRead(config, out var bone);
            if (!subjectFrame.Accept(ready, subjects.CurrentIdentity))
            {
                PauseForSubject(ready ? "Character model changed during portrait submission" : subjects.Status);
                return;
            }
            if (!poseReady)
            {
                // Resolve again at camera submission, after the original native
                // update, instead of framing the pose from before this tick.
                if (lastSubject != subjects.CurrentIdentity
                    || lastBone != config.BoneName || lastOrientation != config.Orientation || lastLockBone != config.LockBone)
                {
                    solver.Reset();
                    lastSubject = subjects.CurrentIdentity;
                    lastBone = config.BoneName;
                    lastOrientation = config.Orientation;
                    lastLockBone = config.LockBone;
                }
                var now = clock.Elapsed.TotalSeconds;
                pose = solver.Solve(bone, config, (float)(now - lastPoseTime));
                Investigation?.RecordPose(bone, pose);
                lastPoseTime = now;
                poseReady = true;
                Status = subjects.Status;
            }
            cameraOverride.Apply(current, pose, sourceWidth, sourceHeight, cropWidth, cropHeight);
            cameraApplied = true;
            TraceEvent("portrait matrices applied");
        }
        catch (Exception e) { cameraOverride.Restore(); Fail(e); }
    }

    private bool CaptureDue()
    {
        // Legacy portraits follow outer game frames, not a separate timer.
        // Finite investigations may deliberately cap captures for comparison.
        if (Backend == RenderBackend.FullTick && (!InvestigationCapture || InvestigationRefreshLimit == 0)) return true;
        var rate = InvestigationCapture && InvestigationRefreshLimit > 0 ? InvestigationRefreshLimit : config.RefreshRate;
        return clock.Elapsed.TotalSeconds - lastCapture >= 1.0 / rate;
    }

    private void SubmitRenderOnly()
    {
        try
        {
            if (Enabled && cycle.TimedOut(clock.Elapsed.TotalSeconds))
                throw new InvalidOperationException("Render-only capture completion timed out");
            if (cycle.HasPending || !CanCapture()
                || !CaptureDue()) return;
            SubjectReady = subjects.TryRead(config, out _);
            Status = subjects.Status;
            if (!SubjectReady) { PauseForSubject(subjects.Status); return; }
            if (!GameTextureSource.TryBackbufferSize(out sourceWidth, out sourceHeight)) return;
            var manager = SceneCameraManager.Instance();
            var current = manager == null || manager->CameraIndex is < 0 or >= 14 ? null : manager->CurrentCamera;
            if (current == null || current->RenderCamera == null) return;
            Investigation?.RecordCamera((nint)current->RenderCamera);
            subjectFrame.Begin(subjects.CurrentIdentity);
            (cropWidth, cropHeight) = PortraitCamera.Crop(sourceWidth, sourceHeight, resolution, aspect);
            if (!cycle.TryBegin(clock.Elapsed.TotalSeconds, out captureId)) return;
            lastCapture = clock.Elapsed.TotalSeconds;
            cameraApplied = captureQueued = poseReady = false;
            var started = Stopwatch.GetTimestamp();
            inPortrait = true;
            try
            {
                renderOnly!.Submit(applyRenderOnlyCamera, queueRenderOnlyMarker);
                RenderOnlySubmissions++;
                suppressRenderOnlyPresent = true;
                // Shared camera data must survive until the render thread has
                // consumed it. This waits for command consumption, not a second
                // Framework tick. Only the prior main frame is presented.
                if (!cycle.WaitForCompletion(captureId, 500))
                    throw new InvalidOperationException("Render-only command completion timed out");
                cameraOverride.Restore();
            }
            finally
            {
                inPortrait = false;
                LastPortraitMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!captureQueued) { cycle.CancelBeforeSubmission(captureId); cameraOverride.Restore(); }
            }
        }
        catch (OperationCanceledException) when (subjectFrame.Unavailable && Fault == null) { }
        catch (Exception e) { Fail(e); }
    }

    private void ApplyRenderOnlyCamera()
    {
        var manager = SceneCameraManager.Instance();
        var current = manager == null || manager->CameraIndex is < 0 or >= 14 ? null : manager->CurrentCamera;
        if (current != null && current->RenderCamera != null) ApplyPortraitCamera((nint)current->RenderCamera);
        if (subjectFrame.Unavailable) throw new OperationCanceledException("Character model changed before render-only submission");
        if (!cameraApplied || !Enabled) throw new InvalidOperationException("Portrait camera unavailable before render-only submission");
        // Only the selected scene camera may prepare its native matrices. This
        // virtual dispatch is camera-local; do not iterate scene objects, whose
        // character updates also register animation and physics jobs.
        current->UpdateRender();
        if (subjectFrame.Unavailable) throw new OperationCanceledException("Character model changed during camera preparation");
        if (!cameraApplied || !Enabled) throw new InvalidOperationException("Portrait camera preparation failed");
    }

    private void QueueRenderOnlyMarker()
    {
        captureQueued = queue.EnqueueCapture(captureId, cropWidth, cropHeight, renderOnly: true);
        if (!captureQueued) throw new InvalidOperationException("Render-only completion marker could not be queued");
    }

    private void LogPipeline()
    {
        if (!Enabled || clock.Elapsed.TotalSeconds - lastDiagnostic < 5) return;
        lastDiagnostic = clock.Elapsed.TotalSeconds;
        var resources = textures.Statistics;
        log.Info($"Pipeline: backend={Backend}, mainViewOnly={MainViewOnly}, normal={NormalTicks}, portrait={PortraitTicks}, renderOnly={RenderOnlySubmissions}, captures={CapturedFrames}, suppressed={SkippedPresents}, mainPresents={MainPresents}, ui={UiDraws}, normalCpuMs={LastNormalMilliseconds:F2}, portraitCpuMs={LastPortraitMilliseconds:F2}, copyCpuMs={LastCopyMilliseconds:F2}, pendingMarkers={queue.PendingCount}, textureSetsCreated={resources.CreatedSets}, liveTextures={resources.LiveTextures}, retiredSets={resources.RetiredSets}, textureMiB={resources.TextureBytes / 1048576.0:F1}, status={Status}, pixels={textures.ProbeResult}");
    }

    private void BeforeUi(AtkServer* server, bool flag)
    {
        using var measurement = Investigation?.Measure(InvestigationSamples.Stage.Ui) ?? default;
        Investigation?.RecordBoneState("BeforeUi", InvestigationActor);
        Interlocked.Increment(ref callbacks);
        try
        {
            if (inPortrait && cameraApplied && !captureQueued && !stopping)
            {
                try
                {
                    captureQueued = queue.EnqueueCapture(captureId, cropWidth, cropHeight);
                    if (TraceEnabled) TraceEvent($"submit id={captureId}, queued={captureQueued}, portrait={inPortrait}");
                }
                catch (Exception e) { Fail(e); }
            }
            // All UI callbacks in the portrait submission belong to this view,
            // including repeated callbacks after the marker has been queued.
            if (inPortrait && cameraApplied && !stopping) return;
            ui!.Original(server, flag);
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    private void UpdateUi3D(nint module)
    {
        Interlocked.Increment(ref callbacks);
        try
        {
            // World-space UI belongs to the displayed game view. Its lists are
            // rebuilt by the normal Tick; the portrait does not submit game UI.
            if (inPortrait && Backend == RenderBackend.FullTick && !stopping)
            {
                SuppressedUi3DUpdates++;
                return;
            }
            ui3DUpdate!.Original(module);
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    private void UpdateUi(nint module, float delta)
    {
        Interlocked.Increment(ref callbacks);
        try
        {
            // Main-first legacy rendering has already updated UI this frame.
            // Keep Draw2D and the completion marker path intact; only reuse the
            // main view's text, macro, addon and world-space UI update results.
            if (inPortrait && Backend == RenderBackend.FullTick && !stopping
                && (InvestigationReuseMainUiUpdate ?? ReuseMainUiUpdate))
            {
                SuppressedUiUpdates++;
                return;
            }
            UiUpdates++;
            uiUpdate!.Original(module, delta);
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
                if (request.RenderOnly)
                {
                    try
                    {
                        var device = Device.Instance();
                        if (device != null && !stopping && Enabled && !subjectFrame.Unavailable && SubjectReady)
                        {
                            var started = Stopwatch.GetTimestamp();
                            SwapChainCapture.CaptureRegion(textures, (Silk.NET.DXGI.IDXGISwapChain*)device->SwapChain->DXGISwapChain,
                                (Silk.NET.Direct3D11.ID3D11DeviceContext*)device->D3D11DeviceContext,
                                request.Width, request.Height, sourceWidth, sourceHeight);
                            LastCopyMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            Investigation?.ExportPortrait(textures, device);
                        }
                    }
                    catch (Exception e) { Fail(e); }
                    finally { cycle.CompleteWithoutPresent(request.Id); }
                    return;
                }
                cycle.CaptureExecuted(request.Id);
                if (TraceEnabled) TraceEvent($"capture id={request.Id}, cpuPortrait={inPortrait}");
                // This marks frame identity only. The scene source used here in
                // 0.1.0.4 was empty on the tested client. Copy the final buffer
                // when this frame reaches Present instead.
                // Native fallback is a real target bind. On success no rebinding is needed.
                return;
            }
            setTarget!.Original(context, command);
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    private void Present(SwapChain* swapChain)
    {
        using var measurement = Investigation?.Measure(InvestigationSamples.Stage.Present) ?? default;
        Interlocked.Increment(ref callbacks);
        try
        {
            var device = Device.Instance();
            if (suppressRenderOnlyPresent && !inPortrait && device != null && device->SwapChain == swapChain)
            {
                // Present precedes this batch's render-thread execution. The
                // previous batch is the portrait and must never be displayed.
                suppressRenderOnlyPresent = false;
                SkippedPresents++;
                return;
            }
            if (device != null && device->SwapChain == swapChain && Enabled)
            {
                if (TraceEnabled && (MainViewOnly || inPortrait || normalAfterPortrait || cycle.HasPending))
                    TraceEvent($"present enter, cpuPortrait={inPortrait}, normalAfterPortrait={normalAfterPortrait}");
                if (MainViewOnly && !cycle.HasPending)
                {
                    try
                    {
                        if (CanCapture() && clock.Elapsed.TotalSeconds - lastCapture >= 1.0 / config.RefreshRate)
                        {
                            var firstCapture = lastCapture < 0;
                            lastCapture = clock.Elapsed.TotalSeconds;
                            var copyStarted = Stopwatch.GetTimestamp();
                            SwapChainCapture.Capture(textures, (Silk.NET.DXGI.IDXGISwapChain*)swapChain->DXGISwapChain,
                                (Silk.NET.Direct3D11.ID3D11DeviceContext*)device->D3D11DeviceContext, resolution, aspect);
                            LastCopyMilliseconds = Stopwatch.GetElapsedTime(copyStarted).TotalMilliseconds;
                            Investigation?.ExportPortrait(textures, device);
                            SubjectReady = true;
                            Status = "Diagnostic: final backbuffer (includes game UI)";
                            if (firstCapture) log.Info($"Final-backbuffer first capture: {textures.ProbeResult}");
                        }
                    }
                    catch (Exception e) { Fail(e); }
                }
            }
            if (device != null && device->SwapChain == swapChain && cycle.ConsumePresent(out var completedId, out var suppress))
            {
                if (TraceEnabled) TraceEvent($"present after marker id={completedId}, suppress={suppress}, cpuPortrait={inPortrait}");
                if (suppress)
                {
                    try
                    {
                        if (!stopping && Enabled && !MainViewOnly && !subjectFrame.Unavailable && SubjectReady)
                        {
                            var copyStarted = Stopwatch.GetTimestamp();
                            SwapChainCapture.CaptureRegion(textures, (Silk.NET.DXGI.IDXGISwapChain*)swapChain->DXGISwapChain,
                                (Silk.NET.Direct3D11.ID3D11DeviceContext*)device->D3D11DeviceContext,
                                cropWidth, cropHeight, sourceWidth, sourceHeight);
                            LastCopyMilliseconds = Stopwatch.GetElapsedTime(copyStarted).TotalMilliseconds;
                            Investigation?.ExportPortrait(textures, device);
                            if (TraceEnabled) TraceEvent($"portrait backbuffer copied id={completedId}, {textures.ProbeResult}");
                        }
                    }
                    catch (Exception e) { Fail(e); }
                    finally { cameraOverride.Restore(); }
                    SkippedPresents++;
                    return;
                }
            }
            present!.Original(swapChain);
            if (device != null && device->SwapChain == swapChain)
            {
                Interlocked.Increment(ref mainPresents);
                // This Present was not suppressed and therefore displayed the
                // main frame, even when the next portrait is already pending.
                Investigation?.ExportMain(device);
            }
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

    private void PauseForSubject(string reason)
    {
        SubjectReady = false;
        solver.Reset();
        lastSubject = default;
        textures.ClearPublished();
        Status = $"Waiting for subject: {reason}";
        // Rendering stays armed. The next ready model starts a fresh frame,
        // recalibrates bone axes, and resumes without a manual restart.
    }

    private bool TraceEnabled => Volatile.Read(ref traceEvents) < 30;

    private void TraceEvent(string message)
    {
        if (!TraceEnabled) return;
        if (Interlocked.Increment(ref traceEvents) <= 30)
            log.Info($"Pipeline event t={clock.Elapsed.TotalMilliseconds:F1} thread={Environment.CurrentManagedThreadId}: {message}");
    }

    private void DisposeHooks()
    {
        uiUpdate?.Dispose(); uiUpdate = null;
        ui3DUpdate?.Dispose(); ui3DUpdate = null;
        gameplayCameraUpdate?.Dispose(); gameplayCameraUpdate = null;
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
        // An idle plugin has no in-flight UI resources. Dispose before its load
        // context unloads, so previously unused Silk methods can still be JITted.
        if (textures.Statistics.LiveTextures == 0 && Volatile.Read(ref callbacks) == 0)
        {
            textures.Dispose();
            cycle.Dispose();
            disposed = true;
            return;
        }
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
        cycle.Dispose();
        disposed = true;
    }
}
