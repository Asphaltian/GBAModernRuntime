using System.Diagnostics;

namespace AGBModern;

internal sealed class FramePacer
{
    private static readonly long TicksPerFrame = Stopwatch.Frequency * Video.CyclesPerFrame / Scheduler.CyclesPerSecond;

    private long _nextFrame = Stopwatch.GetTimestamp();
    private long _longestSleep;

    public void WaitForNextFrame()
    {
        _nextFrame += TicksPerFrame;

        long now = Stopwatch.GetTimestamp();
        if (now > _nextFrame + TicksPerFrame)
        {
            _nextFrame = now;
            return;
        }

        while (_nextFrame - now > _longestSleep)
        {
            Thread.Sleep(1);
            long woke = Stopwatch.GetTimestamp();
            _longestSleep = Math.Max(_longestSleep, woke - now);
            now = woke;
        }

        while (Stopwatch.GetTimestamp() < _nextFrame)
        {
            Thread.SpinWait(50);
        }
    }
}
