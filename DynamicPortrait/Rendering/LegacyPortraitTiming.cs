// SPDX-License-Identifier: AGPL-3.0-or-later
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace DynamicPortrait.Rendering;

// A complete extra Tick still schedules native jobs. Freeze its simulation
// clock without skipping jobs, then let the normal Tick consume elapsed time.
internal readonly unsafe struct LegacyPortraitTiming : IDisposable
{
    private readonly NativeFramework* native;
    private readonly long counter, milliseconds, microseconds;
    private readonly float delta, realDelta, nextDelta, millisecondRemainder, microsecondRemainder;
    private readonly int pause;

    public LegacyPortraitTiming(NativeFramework* native)
    {
        this.native = native;
        counter = native->PerformanceCounterValue;
        delta = native->FrameDeltaTime;
        realDelta = native->RealFrameDeltaTime;
        milliseconds = native->FrameDeltaTimeMSInt;
        microseconds = native->FrameDeltaTimeUSInt;
        millisecondRemainder = native->FrameDeltaTimeMSRem;
        microsecondRemainder = native->FrameDeltaTimeUSRem;
        nextDelta = native->NextFrameDeltaTimeOverride;
        pause = native->PauseFrameTicksCounter;
        // A zero one-shot override means no override. The pause counter is the
        // native mechanism that produces an actual zero simulation delta.
        native->NextFrameDeltaTimeOverride = 0;
        native->PauseFrameTicksCounter = pause == 0 ? 1 : pause;
    }

    public void Dispose()
    {
        native->PerformanceCounterValue = counter;
        native->FrameDeltaTime = delta;
        native->RealFrameDeltaTime = realDelta;
        native->FrameDeltaTimeMSInt = milliseconds;
        native->FrameDeltaTimeUSInt = microseconds;
        native->FrameDeltaTimeMSRem = millisecondRemainder;
        native->FrameDeltaTimeUSRem = microsecondRemainder;
        native->NextFrameDeltaTimeOverride = nextDelta;
        native->PauseFrameTicksCounter = pause;
        // Keep FrameCounter monotonic: both submissions own native frame jobs.
    }
}
