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
    public sealed record Request(DateTime ExpiresUtc, int SecondsPerPhase = 12);
    private readonly PortraitRenderer renderer;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly string directory;
    private readonly int duration;
    private readonly Stopwatch clock = new();
    private readonly InvestigationSamples samples = new();
    private Hook<RenderManager.Delegates.Render>? render;
    private Hook<RenderManager.Delegates.RenderView>? view;
    private Hook<TaskManager.Delegates.ExecuteAllTasks>? tasks;
    private readonly List<object> checkpoints = new();
    private object? taskList;
    private bool active, complete;
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
        ISigScanner scanner, IPluginLog log, string directory, int duration)
    {
        this.renderer = renderer; this.framework = framework; this.log = log;
        this.directory = directory; this.duration = Math.Clamp(duration, 5, 30);
        try
        {
            // Observation hooks invoke only their original function, exactly once.
            render = interop.HookFromAddress<RenderManager.Delegates.Render>(
                scanner.ScanText("40 53 57 41 54 41 55 48 83 EC ?? 65 48 8B 04 25"), Render);
            view = interop.HookFromAddress<RenderManager.Delegates.RenderView>(
                scanner.ScanText("E8 ?? ?? ?? ?? FF C5 49 83 C6 ?? BA"), View);
            tasks = interop.HookFromAddress<TaskManager.Delegates.ExecuteAllTasks>(
                scanner.ScanText("E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9 74 ?? F3 0F 10 8B"), Tasks);
            render.Enable(); view.Enable(); tasks.Enable();
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

    public void BeginFrame(long frame)
    {
        samples.Frame = frame;
        if (active && GameIsForeground()) foregroundFrames[samples.Phase]++;
        if (phaseFrame == 0) { phaseFrame = frame; samples.PhaseFirstFrame = frame; }
    }

    private void Render(RenderManager* manager)
    {
        using var scope = Measure(InvestigationSamples.Stage.Render);
        render!.Original(manager);
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
            renderer.SetEnabled(true);
            log.Info("Render investigation: portrait comparison started (window visibility bypassed for this finite test).");
        }
        if (clock.Elapsed.TotalSeconds < duration * 2) return;
        Finish("completed");
    }

    private void Checkpoint(string label)
    {
        using var process = Process.GetCurrentProcess();
        checkpoints.Add(new { Label = label, Seconds = clock.Elapsed.TotalSeconds,
            renderer.NormalTicks, renderer.PortraitTicks, renderer.CapturedFrames, renderer.SkippedPresents,
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
        renderer.SetEnabled(false);
        framework.Update -= Update;
        Checkpoint(reason);
        var snapshot = samples.Snapshot();
        var report = new { Reason = reason, DurationSeconds = clock.Elapsed.TotalSeconds,
            SecondsPerPhase = duration, PhaseNames = new[] { "baseline", "portrait" },
            Note = "Inclusive wall times include engine waits; this is not GPU execution timing. Background FPS limits may apply.",
            ForegroundFrames = foregroundFrames.ToArray(),
            TaskList = taskList, Checkpoints = checkpoints.ToArray(), snapshot.Summaries, snapshot.FramePacing, snapshot.Events, snapshot.Dropped };
        var path = Path.Combine(directory, $"render-investigation-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        // Serialize and write after sampling on a managed worker; no game API is called there.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
                options.Converters.Add(new JsonStringEnumConverter());
                File.WriteAllText(path, JsonSerializer.Serialize(report, options));
                log.Info($"Render investigation saved: {path}; rendering stopped automatically.");
            }
            catch (Exception e) { log.Error(e, "Could not save render investigation"); }
        });
        tasks?.Disable(); view?.Disable(); render?.Disable();
        renderer.Investigation = null;
    }

    public void Dispose()
    {
        if (active) Finish("unloaded");
        framework.Update -= Update;
        if (renderer.Investigation == this) renderer.Investigation = null;
        tasks?.Dispose(); tasks = null;
        view?.Dispose(); view = null;
        render?.Dispose(); render = null;
    }
}
