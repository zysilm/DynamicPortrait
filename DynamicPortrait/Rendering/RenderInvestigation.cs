// SPDX-License-Identifier: AGPL-3.0-or-later
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using TaskManager = FFXIVClientStructs.FFXIV.Client.System.Framework.TaskManager;

namespace DynamicPortrait.Rendering;

internal sealed unsafe class RenderInvestigation : IDisposable
{
    public sealed record Request(DateTime ExpiresUtc, int SecondsPerPhase = 12,
        RenderBackend Backend = RenderBackend.FullTick, int CaptureLimit = 0, int RefreshLimit = 60, bool ExportFrames = false, bool ReplayGameplayCamera = false,
        bool SpatialAntialiasing = false);
    private readonly PortraitRenderer renderer;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly string directory;
    private readonly int duration;
    private readonly Request request;
    private readonly string imageDirectory;
    private bool mainExported, mainAfterExported, portraitExported;
    private readonly Stopwatch clock = new();
    private readonly InvestigationSamples samples = new();
    private Hook<RenderManager.Delegates.Render>? render;
    private Hook<RenderManager.Delegates.RenderView>? view;
    private Hook<TaskManager.Delegates.ExecuteAllTasks>? tasks;
    private delegate void OrbitDelegate(nint camera, nint target, System.Numerics.Vector3* position, nint swap);
    private Hook<OrbitDelegate>? orbit;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CameraConstantsDelegate(nint camera);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Ui3DUpdateDelegate(nint module);
    private Hook<CameraConstantsDelegate>? cameraConstants;
    private Hook<Ui3DUpdateDelegate>? ui3DUpdate;
    private readonly List<object> sharedStateSamples = new();
    private readonly List<object> ui3DSamples = new();
    private Hook<NativeCallbacks.CameraUpdate>? cameraStateUpdate;
    private readonly List<object> cameraStateSamples = new();
    private readonly List<object> boneStateSamples = new();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CharacterRenderUpdateDelegate(nint character);
    private Hook<CharacterRenderUpdateDelegate>? characterRenderUpdate;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SkeletonListDelegate(nint skeletons, float delta);
    private Hook<SkeletonListDelegate>? skeletonList;
    private readonly List<object> orbitSamples = new();
    private readonly int[] orbitSampleCounts = new int[2];
    private readonly long[,] orbitCalls = new long[2, 2];
    private readonly List<object> checkpoints = new();
    private object? taskList;
    private object? cameraData;
    private object? portraitPose;
    private readonly List<object> tickTiming = new();
    private readonly int[] timingCounts = new int[2];
    private readonly long[] mainCameraCarryover = new long[2];
    private readonly long[] mainMatrixRestores = new long[2], mainAliasedInputs = new long[2], mainInputOverwrites = new long[2];
    private readonly List<object> cameraRestores = new();
    private readonly long[,] gameplayCameraCalls = new long[2, 2], gameplayCameraSkipped = new long[2, 2];
    private readonly double[,] gameplayCameraTotal = new double[2, 2], gameplayCameraMaximum = new double[2, 2];
    private bool active, complete;
    internal bool IsRunning => active;
    internal bool IsBaseline => active && samples.Phase == 0;
    private long phaseFrame;
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    private readonly long[] foregroundFrames = new long[2];
    private static bool GameIsForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var process);
        return process == Environment.ProcessId;
    }

    public RenderInvestigation(PortraitRenderer renderer, IFramework framework, IGameInteropProvider interop,
        ISigScanner scanner, IPluginLog log, string directory, Request request)
    {
        this.renderer = renderer; this.framework = framework; this.log = log;
        this.directory = directory; this.request = request; duration = Math.Clamp(request.SecondsPerPhase, 5, 30);
        imageDirectory = Path.Combine(directory, $"frames-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        try
        {
            // Observation hooks invoke only their original function, exactly once.
            render = interop.HookFromAddress<RenderManager.Delegates.Render>(
                scanner.ScanText("40 53 57 41 54 41 55 48 83 EC ?? 65 48 8B 04 25"), Render);
            view = interop.HookFromAddress<RenderManager.Delegates.RenderView>(
                scanner.ScanText("E8 ?? ?? ?? ?? FF C5 49 83 C6 ?? BA"), View);
            tasks = interop.HookFromAddress<TaskManager.Delegates.ExecuteAllTasks>(
                scanner.ScanText("E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9 74 ?? F3 0F 10 8B"), Tasks);
            var gameManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
            if (gameManager != null && gameManager->Camera != null)
            {
                var table = *(nint**)gameManager->Camera;
                cameraStateUpdate = interop.HookFromAddress<NativeCallbacks.CameraUpdate>(table[2], CameraStateUpdate);
                cameraStateUpdate.Enable();
                var lookAt = scanner.ScanText("40 53 48 83 EC 30 44 8B 89 ?? ?? ?? ?? 48 8B DA");
                for (var i = 0; i < 63; i++)
                    if (table[i] == lookAt)
                    {
                        orbit = interop.HookFromAddress<OrbitDelegate>(table[i + 1], Orbit);
                        orbit.Enable();
                        break;
                    }
            }
            render.Enable(); view.Enable(); tasks.Enable();
            skeletonList = interop.HookFromAddress<SkeletonListDelegate>(scanner.ScanText(
                "E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? 48 8B 6C 24 ?? 48 8B 5C 24"), SkeletonList);
            skeletonList.Enable();
            var actor = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)renderer.InvestigationActor;
            if (actor != null && actor->DrawObject != null
                && actor->DrawObject->GetObjectType() == FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
            {
                var table = *(nint**)actor->DrawObject;
                characterRenderUpdate = interop.HookFromAddress<CharacterRenderUpdateDelegate>(table[4], CharacterRenderUpdate);
                characterRenderUpdate.Enable();
            }
            var cameras = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
            if (cameras != null && cameras->CurrentCamera != null && cameras->CurrentCamera->RenderCamera != null)
            {
                var table = *(nint**)cameras->CurrentCamera->RenderCamera;
                cameraConstants = interop.HookFromAddress<CameraConstantsDelegate>(table[6], CameraConstants);
                cameraConstants.Enable();
            }
            // UI3DModule.Update resets and rebuilds world-space UI lists.
            ui3DUpdate = interop.HookFromAddress<Ui3DUpdateDelegate>(scanner.ScanText(
                "53 56 57 48 83 EC 30 33 F6 48 89 6C 24 50 4C 89 74 24 60 48 8B F9"), Ui3DUpdate);
            ui3DUpdate.Enable();
            renderer.Investigation = this;
            renderer.PrepareInvestigation();
            active = true;
            clock.Start();
            Checkpoint("start");
            framework.Update += Update;
            log.Info($"Render investigation started: {this.duration}s baseline + {this.duration}s portrait; report directory: {directory}");
        }
        catch { Dispose(); throw; }
    }

    public InvestigationSamples.Scope Measure(InvestigationSamples.Stage stage, int argument = 0)
        => active ? samples.Measure(stage, argument) : default;

    private static ulong StateHash(nint address, int length)
    {
        ulong hash = 14695981039346656037;
        foreach (var value in new ReadOnlySpan<byte>((void*)address, length))
            hash = unchecked((hash ^ value) * 1099511628211);
        return hash;
    }

    private static object CameraState(nint camera)
    {
        // Verified against the inspected executable's camera constant update.
        // These are observation-only fields, not a writable SDK structure.
        var sequence = *(nint*)(camera + 0x278);
        var graphics = FFXIVClientStructs.FFXIV.Client.Graphics.Render.GraphicsConfig.Instance();
        return new { View = StateHash(camera + 0x10, 64),
            History = StateHash(camera + 0xA0, 256),
            PreviousView = StateHash(camera + 0x120, 64),
            PreviousProjection = StateHash(camera + 0x160, 64),
            Projection = StateHash(camera + 0x50, 64),
            Sequence = sequence == 0 ? (uint?)null : *(uint*)(sequence + 0x10),
            OffsetX = sequence == 0 ? (float?)null : *(float*)(sequence + 8),
            OffsetY = sequence == 0 ? (float?)null : *(float*)(sequence + 12),
            TemporalReset = graphics != null && graphics->ResetTemporalHistory,
            PendingTemporalReset = graphics != null && (*(ulong*)((byte*)graphics + 0x80) & 1) != 0,
            AntiAliasing = graphics == null ? (uint?)null : graphics->AntiAliasing,
            UpscaleType = graphics == null ? (byte?)null : graphics->GraphicsRezoUpscaleType };
    }

    private static object GameplayState(FFXIVClientStructs.FFXIV.Client.Game.Camera* camera)
        => new { ShouldResetAngles = camera->ShouldResetAngles, SavedModelSkeletonId = camera->SavedModelSkeletonId,
            ZoomMode = (int)camera->ZoomMode, ControlMode = (int)camera->ControlMode,
            Distance = camera->Distance, InterpDistance = camera->InterpDistance,
            DirH = camera->DirH, DirV = camera->DirV,
            Position = new System.Numerics.Vector3(camera->SceneCamera.Position.X,
                camera->SceneCamera.Position.Y, camera->SceneCamera.Position.Z) };

    private void CameraStateUpdate(FFXIVClientStructs.FFXIV.Client.Game.CameraBase* camera)
    {
        var manager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
        var observe = active && cameraStateSamples.Count < 2048 && manager != null
            && (nint)manager->Camera == (nint)camera;
        var before = observe ? GameplayState((FFXIVClientStructs.FFXIV.Client.Game.Camera*)camera) : null;
        cameraStateUpdate!.Original(camera);
        if (observe) cameraStateSamples.Add(new { Phase = samples.Phase, Frame = samples.Frame,
            Portrait = renderer.IsPortraitTick, Before = before,
            After = GameplayState((FFXIVClientStructs.FFXIV.Client.Game.Camera*)camera) });
    }

    private void CameraConstants(nint camera)
    {
        var cameras = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
        var observe = active && sharedStateSamples.Count < 2048 && cameras != null
            && cameras->CurrentCamera != null && (nint)cameras->CurrentCamera->RenderCamera == camera;
        var before = observe ? CameraState(camera) : null;
        cameraConstants!.Original(camera);
        if (observe) sharedStateSamples.Add(new { Phase = samples.Phase, Frame = samples.Frame,
            Portrait = renderer.IsPortraitTick, Before = before, After = CameraState(camera) });
    }

    private void Ui3DUpdate(nint module)
    {
        var skipped = renderer.SuppressedUi3DUpdates;
        ui3DUpdate!.Original(module);
        if (!active || ui3DSamples.Count >= 2048) return;
        var state = (FFXIVClientStructs.FFXIV.Client.UI.UI3DModule*)module;
        ui3DSamples.Add(new { Phase = samples.Phase, Frame = samples.Frame,
            Portrait = renderer.IsPortraitTick, Suppressed = renderer.SuppressedUi3DUpdates != skipped,
            SortedObjectInfoCount = state->SortedObjectInfoCount,
            NamePlateObjectInfoCount = state->NamePlateObjectInfoCount, CharacterObjectInfoCount = state->CharacterObjectInfoCount,
            ObjectInfoHash = StateHash(module + 0x20, 819 * 0x60) });
    }

    public void BeginFrame(long frame)
    {
        samples.Frame = frame;
        if (active && GameIsForeground()) foregroundFrames[samples.Phase]++;
        if (phaseFrame == 0) { phaseFrame = frame; samples.PhaseFirstFrame = frame; }
    }

    public void ExportMain(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device* device)
    {
        if (!active || !request.ExportFrames || samples.Frame < 5) return;
        string name;
        if (samples.Phase == 0 && !mainExported) { mainExported = true; name = "main.png"; }
        else if (samples.Phase == 1 && !mainAfterExported && renderer.CapturedFrames >= 2)
        { mainAfterExported = true; name = "main-after.png"; }
        else return;
        try { SwapChainCapture.Export((Silk.NET.DXGI.IDXGISwapChain*)device->SwapChain->DXGISwapChain,
            (Silk.NET.Direct3D11.ID3D11DeviceContext*)device->D3D11DeviceContext, Path.Combine(imageDirectory, name)); }
        catch (Exception e) { log.Warning(e, "Could not export main investigation frame"); }
    }

    public void RecordGameplayCameraUpdate(bool portrait, bool suppressed, double milliseconds)
    {
        if (!active) return;
        var index = portrait ? 1 : 0;
        gameplayCameraCalls[samples.Phase, index]++;
        if (suppressed) gameplayCameraSkipped[samples.Phase, index]++;
        gameplayCameraTotal[samples.Phase, index] += milliseconds;
        gameplayCameraMaximum[samples.Phase, index] = Math.Max(gameplayCameraMaximum[samples.Phase, index], milliseconds);
    }

    public void RecordMainCameraCarryover(bool activeOverride)
    {
        if (active && activeOverride) mainCameraCarryover[samples.Phase]++;
    }

    public void RecordMatrixRestore(nint camera, bool portrait, bool aliasesScene, System.Numerics.Matrix4x4 incoming, System.Numerics.Matrix4x4 restored)
    {
        if (!active) return;
        var changed = incoming != restored;
        if (!portrait)
        {
            mainMatrixRestores[samples.Phase]++;
            if (aliasesScene) mainAliasedInputs[samples.Phase]++;
            if (changed) mainInputOverwrites[samples.Phase]++;
        }
        if (cameraRestores.Count < 64)
            cameraRestores.Add(new { Phase = samples.Phase, Frame = samples.Frame, Portrait = portrait,
                AliasesScene = aliasesScene, InputOverwritten = changed, Incoming = incoming, Restored = restored,
                CameraAfterRestore = CameraState(camera) });
    }

    public void RecordCamera(nint camera)
    {
        if (cameraData != null) return;
        using var process = Process.GetCurrentProcess();
        var module = process.MainModule!.BaseAddress;
        var table = Marshal.ReadIntPtr(camera);
        cameraData = new { VtableRva = $"0x{(long)(table - module):X}",
            Functions = Enumerable.Range(0, 8).Select(i => $"0x{(long)(Marshal.ReadIntPtr(table + i * 8) - module):X}").ToArray() };
    }

    public void RecordPose(DynamicPortrait.Camera.BonePose bone, DynamicPortrait.Camera.CameraPose camera)
    {
        portraitPose ??= new { Bone = bone, Camera = camera };
    }

    public void RecordBoneState(string stage, nint actorAddress)
    {
        if (!active || actorAddress == 0 || boneStateSamples.Count >= 8192) return;
        var actor = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actorAddress;
        var draw = actor->DrawObject;
        if (draw == null || draw->GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase) return;
        var skeleton = ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)draw)->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount == 0) return;
        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null || pose->ModelPose.Data == null
            || pose->Skeleton->Bones.Length > 1024) return;
        var root = skeleton->Transform;
        var position = new System.Numerics.Vector3(root.Position.X, root.Position.Y, root.Position.Z);
        var rotation = new System.Numerics.Quaternion(root.Rotation.X, root.Rotation.Y, root.Rotation.Z, root.Rotation.W);
        var bones = new List<object>();
        for (var i = 0; i < Math.Min(pose->Skeleton->Bones.Length, pose->ModelPose.Length); i++)
        {
            var name = pose->Skeleton->Bones[i].Name.String;
            if (name is not ("j_sebo_a" or "j_sebo_c" or "j_kao")) continue;
            var translation = pose->ModelPose[i].Translation;
            var local = new System.Numerics.Vector3(translation.X, translation.Y, translation.Z);
            bones.Add(new { Name = name, Local = local,
                World = position + System.Numerics.Vector3.Transform(local, rotation),
                Flags = i < pose->BoneFlags.Length ? (uint?)pose->BoneFlags[i] : null });
        }
        // Observe cached pose data only. Synchronizing here would change the
        // animation state whose ordering this probe is intended to measure.
        boneStateSamples.Add(new { Stage = stage, Phase = samples.Phase, Frame = samples.Frame,
            Portrait = renderer.IsPortraitTick, Actor = $"0x{actorAddress:X}",
            ModelInSync = pose->ModelInSync, LocalInSync = pose->LocalInSync,
            Root = position, Bones = bones.ToArray(),
            DeltaSeconds = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->FrameDeltaTime });
    }

    private void CharacterRenderUpdate(nint character)
    {
        var address = renderer.InvestigationActor;
        var actor = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        var observe = active && actor != null && (nint)actor->DrawObject == character;
        if (observe) RecordBoneState("BeforeCharacterRenderUpdate", address);
        characterRenderUpdate!.Original(character);
        if (observe) RecordBoneState("AfterCharacterRenderUpdate", renderer.InvestigationActor);
    }

    private void SkeletonList(nint skeletons, float delta)
    {
        RecordBoneState("BeforeSkeletonList", renderer.InvestigationActor);
        skeletonList!.Original(skeletons, delta);
        RecordBoneState("AfterSkeletonList", renderer.InvestigationActor);
    }

    public void ExportPortrait(PortraitTextures textures, FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device* device)
    {
        if (!active || !request.ExportFrames || portraitExported) return;
        portraitExported = true;
        try { textures.ExportPublished((Silk.NET.Direct3D11.ID3D11DeviceContext*)device->D3D11DeviceContext,
            Path.Combine(imageDirectory, "portrait.png")); }
        catch (Exception e) { log.Warning(e, "Could not export portrait investigation frame"); }
    }

    private void Orbit(nint camera, nint target, System.Numerics.Vector3* position, nint swap)
    {
        var portrait = renderer.IsPortraitTick;
        var started = Stopwatch.GetTimestamp();
        orbit!.Original(camera, target, position, swap);
        RecordBoneState("Orbit", target);
        if (!active) return;
        orbitCalls[samples.Phase, portrait ? 1 : 0]++;
        if (position != null && orbitSampleCounts[samples.Phase] < 2048)
        {
            var gameCamera = (FFXIVClientStructs.FFXIV.Client.Game.Camera*)camera;
            orbitSampleCounts[samples.Phase]++;
            var actor = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target;
            var actorPosition = actor != null ? actor->Position : default;
            var draw = actor != null ? actor->DrawObject : null;
            var modelPosition = draw != null ? draw->Position : default;
            var rootPosition = default(FFXIVClientStructs.FFXIV.Common.Math.Vector3);
            if (draw != null && draw->GetObjectType() == FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
            {
                var skeleton = ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)draw)->Skeleton;
                if (skeleton != null) rootPosition = skeleton->Transform.Position;
            }
            var scene = gameCamera->SceneCamera.Position;
            var previous = gameCamera->LastPosition;
            orbitSamples.Add(new { Phase = samples.Phase, Frame = samples.Frame, Portrait = portrait,
                Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ActorPosition = new System.Numerics.Vector3(actorPosition.X, actorPosition.Y, actorPosition.Z),
                ModelPosition = new System.Numerics.Vector3(modelPosition.X, modelPosition.Y, modelPosition.Z),
                SkeletonRoot = new System.Numerics.Vector3(rootPosition.X, rootPosition.Y, rootPosition.Z),
                Center = *position, TargetAddress = $"0x{target:X}",
                ScenePosition = new System.Numerics.Vector3(scene.X, scene.Y, scene.Z),
                LastPosition = new System.Numerics.Vector3(previous.X, previous.Y, previous.Z) });
        }
    }

    private void Render(RenderManager* manager)
    {
        using var scope = Measure(InvestigationSamples.Stage.Render);
        RecordBoneState("BeforeRender", renderer.InvestigationActor);
        render!.Original(manager);
        RecordBoneState("AfterRender", renderer.InvestigationActor);
    }
    private void View(RenderManager* manager, bool enabled, RenderManager.RenderViews index)
    {
        using var scope = Measure(InvestigationSamples.Stage.RenderView, (int)index);
        view!.Original(manager, enabled, index);
    }
    private void Tasks(TaskManager* manager, float* dt)
    {
        using var scope = Measure(InvestigationSamples.Stage.Tasks);
        // Read only verified task fields; never invoke or retain a task pointer.
        if (taskList == null && manager != null && manager->TaskList != null && manager->TaskCount <= 128)
        {
            var list = new List<object>();
            using var process = Process.GetCurrentProcess();
            var module = process.MainModule!.BaseAddress;
            for (var i = 0; i < manager->TaskCount; i++)
            {
                var root = &manager->TaskList[i].Task;
                var seen = new HashSet<nint>();
                var task = root->Next;
                for (var chain = 0; task != null && task != root && chain < 64 && seen.Add((nint)task); chain++, task = task->Next)
                {
                    if (task->Func != null)
                        list.Add(new { Priority = i, Chain = chain, FunctionRva = $"0x{(long)((nint)task->Func - module):X}" });
                }
            }
            taskList = list;
        }
        if (active && timingCounts[samples.Phase] < 128)
        {
            var native = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (native != null && dt != null)
            {
                timingCounts[samples.Phase]++;
                tickTiming.Add(new { Phase = samples.Phase, Frame = samples.Frame,
                    Portrait = renderer.IsPortraitTick, NativeFrame = native->FrameCounter,
                    TaskDeltaSeconds = *dt, RealDeltaSeconds = native->RealFrameDeltaTime,
                    PerformanceCounter = native->PerformanceCounterValue,
                    PerformanceCounterFrequency = native->PerformanceCounterFrequency });
            }
        }
        tasks!.Original(manager, dt);
    }

    private void Update(IFramework _)
    {
        if (!active) return;
        if (clock.Elapsed.TotalSeconds >= duration && samples.Phase == 0)
        {
            Checkpoint("baseline-end");
            samples.Phase = 1;
            phaseFrame = 0;
            samples.PhaseFirstFrame = samples.Frame + 1;
            renderer.InvestigationCapture = true;
            renderer.InvestigationBackend = request.Backend;
            renderer.InvestigationReplayGameplayCamera = request.ReplayGameplayCamera;
            renderer.InvestigationRefreshLimit = Math.Clamp(request.RefreshLimit, 0, 60);
            renderer.InvestigationSpatialAntialiasing = request.SpatialAntialiasing;
            renderer.SetEnabled(true);
            log.Info("Render investigation: portrait comparison started (window visibility bypassed for this finite test).");
        }
        if (clock.Elapsed.TotalSeconds < duration * 2 && (request.CaptureLimit <= 0 || renderer.CapturedFrames < request.CaptureLimit)) return;
        Finish("completed");
    }

    private void Checkpoint(string label)
    {
        using var process = Process.GetCurrentProcess();
        checkpoints.Add(new { Label = label, Seconds = clock.Elapsed.TotalSeconds,
            renderer.NormalTicks, renderer.PortraitTicks, renderer.CapturedFrames, renderer.SkippedPresents,
            renderer.RenderOnlySubmissions,
            renderer.GameplayCameraUpdates, renderer.SuppressedGameplayCameraUpdates,
            renderer.SuppressedUi3DUpdates,
            renderer.MainPresents, renderer.PendingMarkers, renderer.TextureStatistics,
            renderer.LastNormalMilliseconds, renderer.LastPortraitMilliseconds, renderer.LastCopyMilliseconds,
            renderer.Status, renderer.Fault, PrivateBytes = process.PrivateMemorySize64, Handles = process.HandleCount,
            ProcessCpuSeconds = process.TotalProcessorTime.TotalSeconds,
            GameIsForeground = GameIsForeground() });
    }

    private void Finish(string reason)
    {
        if (complete) return;
        complete = true; active = false;
        renderer.InvestigationCapture = false;
        renderer.InvestigationBackend = null;
        renderer.InvestigationReplayGameplayCamera = false;
        renderer.InvestigationSpatialAntialiasing = false;
        renderer.InvestigationRefreshLimit = 60;
        renderer.SetEnabled(false);
        framework.Update -= Update;
        Checkpoint(reason);
        var snapshot = samples.Snapshot();
        var cameraSummaries = new List<object>();
        for (var phase = 0; phase < 2; phase++)
            for (var index = 0; index < 2; index++)
                if (gameplayCameraCalls[phase, index] > 0)
                    cameraSummaries.Add(new { Phase = phase, Portrait = index == 1,
                        Calls = gameplayCameraCalls[phase, index], Skipped = gameplayCameraSkipped[phase, index],
                        MeanMilliseconds = gameplayCameraTotal[phase, index] / gameplayCameraCalls[phase, index],
                        MaxMilliseconds = gameplayCameraMaximum[phase, index] });
        var report = new { Reason = reason, DurationSeconds = clock.Elapsed.TotalSeconds,
            SecondsPerPhase = duration, PhaseNames = new[] { "baseline", "portrait" },
            Backend = request.Backend.ToString(), RefreshLimit = request.RefreshLimit,
            request.ReplayGameplayCamera, request.SpatialAntialiasing, GameplayCameraSummaries = cameraSummaries.ToArray(),
            FramesDirectory = request.ExportFrames ? imageDirectory : null,
            Note = "Inclusive wall times include engine waits; this is not GPU execution timing. Background FPS limits may apply.",
            ForegroundFrames = foregroundFrames.ToArray(),
            SharedStateSamples = sharedStateSamples.ToArray(), Ui3DSamples = ui3DSamples.ToArray(),
            CameraStateSamples = cameraStateSamples.ToArray(),
            BoneStateSamples = boneStateSamples.ToArray(),
            OrbitSamples = orbitSamples.ToArray(),
            OrbitCalls = new[] { new { Phase = 0, Normal = orbitCalls[0, 0], Portrait = orbitCalls[0, 1] },
                new { Phase = 1, Normal = orbitCalls[1, 0], Portrait = orbitCalls[1, 1] } },
            MainCameraCarryover = mainCameraCarryover.ToArray(), MainMatrixRestores = mainMatrixRestores.ToArray(),
            MainAliasedInputs = mainAliasedInputs.ToArray(), MainInputOverwrites = mainInputOverwrites.ToArray(), CameraRestores = cameraRestores.ToArray(),
            TickTiming = tickTiming.ToArray(), TaskList = taskList, CameraData = cameraData, PortraitPose = portraitPose, Checkpoints = checkpoints.ToArray(), snapshot.Summaries, snapshot.FramePacing, snapshot.Events, snapshot.Dropped };
        var path = Path.Combine(directory, $"render-investigation-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        // Serialize and write after sampling on a managed worker; no game API is called there.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true,
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
                options.Converters.Add(new JsonStringEnumConverter());
                File.WriteAllText(path, JsonSerializer.Serialize(report, options));
                log.Info($"Render investigation saved: {path}; rendering stopped automatically.");
            }
            catch (Exception e) { log.Error(e, "Could not save render investigation"); }
        });
        orbit?.Disable();
        cameraConstants?.Disable(); ui3DUpdate?.Disable();
        cameraStateUpdate?.Disable();
        characterRenderUpdate?.Disable();
        skeletonList?.Disable();
        tasks?.Disable(); view?.Disable(); render?.Disable();
        renderer.Investigation = null;
    }

    public void Dispose()
    {
        if (active) Finish("unloaded");
        framework.Update -= Update;
        if (renderer.Investigation == this) renderer.Investigation = null;
        orbit?.Dispose(); orbit = null;
        cameraConstants?.Dispose(); cameraConstants = null;
        cameraStateUpdate?.Dispose(); cameraStateUpdate = null;
        characterRenderUpdate?.Dispose(); characterRenderUpdate = null;
        skeletonList?.Dispose(); skeletonList = null;
        ui3DUpdate?.Dispose(); ui3DUpdate = null;
        tasks?.Dispose(); tasks = null;
        view?.Dispose(); view = null;
        render?.Dispose(); render = null;
    }
}
