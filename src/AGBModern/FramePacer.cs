using System.Diagnostics;

namespace AGBModern;

internal sealed class FramePacer
{
    private static readonly long TicksPerFrame = Stopwatch.Frequency * Video.CyclesPerFrame / Scheduler.CyclesPerSecond;

    private long _nextFrame = Stopwatch.GetTimestamp();

    public void WaitForNextFrame()
    {
        _nextFrame += TicksPerFrame;

        long now = Stopwatch.GetTimestamp();
        if (now > _nextFrame + TicksPerFrame)
        {
            _nextFrame = now;
            return;
        }

        long sleepUntil = _nextFrame - (Stopwatch.Frequency / 500);
        if (now < sleepUntil)
        {
            Thread.Sleep(TimeSpan.FromSeconds((double)(sleepUntil - now) / Stopwatch.Frequency));
        }

        while (Stopwatch.GetTimestamp() < _nextFrame)
        {
            Thread.SpinWait(50);
        }
    }
}
