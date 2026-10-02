// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;

namespace DynamicPortrait.Rendering;

internal sealed class InvestigationSamples
{
    public enum Stage { Tick, Tasks, Render, RenderView, Matrices, Ui, Present }
    public record Event(int Phase, long Frame, Stage Stage, int Argument, int Thread, bool Enter, double Milliseconds);
    public record Summary(int Phase, Stage Stage, long Calls, double MeanMilliseconds, double MaxMilliseconds);
    public record FramePacing(int Phase, int Samples, double MedianMilliseconds, double P95Milliseconds);
    private readonly object gate = new();
    private readonly Event?[] events = new Event[4096];
    private readonly long[,] calls = new long[2, 7];
    private readonly double[,] total = new double[2, 7], maximum = new double[2, 7];
    private readonly double[][] frameTimes = [new double[4096], new double[4096]];
    private readonly int[] frameCount = new int[2];
    private readonly long started = Stopwatch.GetTimestamp();
    private int count, dropped;
    public int Phase;
    public long Frame;
    public long PhaseFirstFrame;

    public Scope Measure(Stage stage, int argument = 0) => new(this, stage, argument);

    private void Record(int phase, long frame, Stage stage, int argument, int thread, bool enter, long timestamp)
    {
        if (frame - PhaseFirstFrame >= 8) return;
        lock (gate)
        {
            if (count == events.Length) { dropped++; return; }
            events[count++] = new(phase, frame, stage, argument, thread, enter,
                Stopwatch.GetElapsedTime(started, timestamp).TotalMilliseconds);
        }
    }

    public (Event[] Events, Summary[] Summaries, FramePacing[] FramePacing, int Dropped) Snapshot()
    {
        lock (gate)
        {
            var summaries = new List<Summary>();
            for (var p = 0; p < 2; p++)
                for (var s = 0; s < 7; s++)
                    if (calls[p, s] > 0) summaries.Add(new(p, (Stage)s, calls[p, s], total[p, s] / calls[p, s], maximum[p, s]));
            var pacing = new List<FramePacing>();
            for (var p = 0; p < 2; p++)
            {
                if (frameCount[p] == 0) continue;
                var values = frameTimes[p].Take(frameCount[p]).Order().ToArray();
                pacing.Add(new(p, values.Length, values[(values.Length - 1) / 2], values[(int)((values.Length - 1) * 0.95)]));
            }
            return (events.Take(count).Select(e => e!).ToArray(), summaries.ToArray(), pacing.ToArray(), dropped);
        }
    }

    public readonly struct Scope : IDisposable
    {
        private readonly InvestigationSamples? owner;
        private readonly Stage stage;
        private readonly int phase, argument, thread;
        private readonly long frame, started;
        public Scope(InvestigationSamples owner, Stage stage, int argument)
        {
            this.owner = owner; this.stage = stage; this.argument = argument;
            phase = owner.Phase; frame = owner.Frame; thread = Environment.CurrentManagedThreadId;
            started = Stopwatch.GetTimestamp();
            owner.Record(phase, frame, stage, argument, thread, true, started);
        }
        public void Dispose()
        {
            if (owner == null) return;
            var end = Stopwatch.GetTimestamp();
            var ms = Stopwatch.GetElapsedTime(started, end).TotalMilliseconds;
            lock (owner.gate)
            {
                owner.calls[phase, (int)stage]++;
                owner.total[phase, (int)stage] += ms;
                owner.maximum[phase, (int)stage] = Math.Max(owner.maximum[phase, (int)stage], ms);
                if (stage == Stage.Tick && owner.frameCount[phase] < owner.frameTimes[phase].Length)
                    owner.frameTimes[phase][owner.frameCount[phase]++] = ms;
            }
            owner.Record(phase, frame, stage, argument, thread, false, end);
        }
    }
}
