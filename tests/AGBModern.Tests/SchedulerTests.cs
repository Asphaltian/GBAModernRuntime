namespace AGBModern.Tests;

public class SchedulerTests
{
    [Fact]
    public void UntimedWorkTakesNoCyclesAndReachesNoEvent()
    {
        bool ran = false;
        var scheduledEvent = Scheduler.CreateEvent(_ => ran = true);
        scheduledEvent.Schedule(100);
        long before = Scheduler.Cycles;

        Scheduler.RunUntimed(() =>
        {
            Assert.Equal(long.MaxValue, Scheduler.NextEvent);
            Scheduler.Cycles += 1000;
            Memory.Read32(0x02000000);
        });

        Assert.Equal(before, Scheduler.Cycles);
        Assert.True(Scheduler.NextEvent <= before + 100);
        Assert.False(ran);

        scheduledEvent.Cancel();
    }

    [Fact]
    public void UntimedWorkInsideUntimedWorkStillReachesNoEvent()
    {
        bool ran = false;
        var scheduledEvent = Scheduler.CreateEvent(_ => ran = true);
        scheduledEvent.Schedule(100);
        var laterEvent = Scheduler.CreateEvent(_ => ran = true);
        long before = Scheduler.Cycles;

        Scheduler.RunUntimed(() =>
        {
            Scheduler.RunUntimed(() => Scheduler.Cycles += 50);
            Assert.Equal(long.MaxValue, Scheduler.NextEvent);

            laterEvent.Schedule(10);
            Assert.Equal(long.MaxValue, Scheduler.NextEvent);

            Scheduler.Cycles += 1000;
            Scheduler.RunDueEvents();
        });

        Assert.Equal(before, Scheduler.Cycles);
        Assert.True(Scheduler.NextEvent <= before + 100);
        Assert.False(ran);

        scheduledEvent.Cancel();
        laterEvent.Cancel();
    }
}
