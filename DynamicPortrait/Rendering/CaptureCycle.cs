// SPDX-License-Identifier: AGPL-3.0-or-later
namespace DynamicPortrait.Rendering;

/// <summary>One outstanding portrait; CPU submission and render-thread completion are independent.</summary>
internal sealed class CaptureCycle : IDisposable
{
    private readonly object gate = new();
    private long sequence;
    private long active;
    private long awaitingPresent;
    private double startedAt;
    private bool suppressPresent;
    private readonly ManualResetEventSlim completed = new(false);
    private long completedId;
    public bool HasPending { get { lock (gate) return active != 0; } }

    public bool TryBegin(double now, out long id, bool suppress = true)
    {
        lock (gate)
        {
            id = 0;
            if (active != 0) return false;
            id = active = ++sequence;
            completed.Reset();
            startedAt = now;
            suppressPresent = suppress;
            return true;
        }
    }

    public void CaptureExecuted(long id)
    {
        lock (gate)
        {
            if (active == id) awaitingPresent = id;
        }
    }

    public bool ConsumePortraitPresent() => ConsumePresent(out var suppress) && suppress;

    public void CompleteWithoutPresent(long id)
    {
        lock (gate)
        {
            if (active != id) return;
            active = awaitingPresent = 0;
            Volatile.Write(ref completedId, id);
            completed.Set();
        }
    }

    public bool WaitForCompletion(long id, int milliseconds)
        => completed.Wait(milliseconds) && Volatile.Read(ref completedId) == id;

    public bool ConsumePresent(out bool suppress)
        => ConsumePresent(out _, out suppress);

    public bool ConsumePresent(out long id, out bool suppress)
    {
        lock (gate)
        {
            id = awaitingPresent;
            suppress = suppressPresent;
            if (awaitingPresent == 0) return false;
            active = awaitingPresent = 0;
            return true;
        }
    }

    public void CancelBeforeSubmission(long id)
    {
        lock (gate)
            if (active == id && awaitingPresent == 0) active = 0;
    }

    public bool TimedOut(double now)
    {
        lock (gate) return active != 0 && now - startedAt > 3;
    }

    public void Dispose() => completed.Dispose();
}
