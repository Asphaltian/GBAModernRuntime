[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AGBModern.Tests;

public class TimersTests
{
    private const uint TM0CNT_L = 0x04000100;
    private const uint TM1CNT_L = 0x04000104;
    private const uint IF = 0x04000202;

    private static void Advance(long cycles)
    {
        Scheduler.Cycles += cycles;
        Scheduler.RunDueEvents();
    }

    private static void StopTimers()
    {
        IO.Write16(TM0CNT_L + 2, 0);
        IO.Write16(TM1CNT_L + 2, 0);
        IO.Write16(IF, 0xFFFF);
    }

    [Fact]
    public void ARunningTimerCountsCyclesFromItsReloadValue()
    {
        IO.Write16(TM0CNT_L, 0xFF00);
        IO.Write16(TM0CNT_L + 2, 0x0080);

        Advance(0x80);
        Assert.Equal(0xFF80, IO.Read16(TM0CNT_L));

        StopTimers();
        Advance(0x80);
        Assert.Equal(0xFF80, IO.Read16(TM0CNT_L));
    }

    [Fact]
    public void AByteWriteKeepsTheOtherByteOfAWriteOnlyRegister()
    {
        IO.Write16(TM0CNT_L, 0xFF00);
        IO.Write8(TM0CNT_L, 0x80);
        IO.Write16(TM0CNT_L + 2, 0x0080);

        Assert.Equal(0xFF80, IO.Read16(TM0CNT_L));

        StopTimers();
    }

    [Fact]
    public void ThePrescalerDividesTheClock()
    {
        IO.Write16(TM0CNT_L, 0);
        IO.Write16(TM0CNT_L + 2, 0x0081);

        Advance(64 * 5);
        Assert.Equal(5, IO.Read16(TM0CNT_L));

        StopTimers();
    }

    [Fact]
    public void AnOverflowReloadsTheCounterAndRaisesTheInterrupt()
    {
        IO.Write16(TM0CNT_L, 0xFFF0);
        IO.Write16(TM0CNT_L + 2, 0x00C0);

        Advance(0x0F);
        Assert.Equal(0, IO.Read16(IF) & (ushort)Interrupt.Timer0);

        Advance(0x04);
        Assert.Equal((ushort)Interrupt.Timer0, IO.Read16(IF) & (ushort)Interrupt.Timer0);
        Assert.Equal(0xFFF3, IO.Read16(TM0CNT_L));

        StopTimers();
    }

    [Fact]
    public void ACountUpTimerCountsOverflowsOfTheTimerBelow()
    {
        IO.Write16(TM1CNT_L, 0xFFFE);
        IO.Write16(TM1CNT_L + 2, 0x00C4);
        IO.Write16(TM0CNT_L, 0xFFF0);
        IO.Write16(TM0CNT_L + 2, 0x0080);

        Advance(0x10);
        Assert.Equal(0xFFFF, IO.Read16(TM1CNT_L));
        Assert.Equal(0, IO.Read16(IF) & (ushort)Interrupt.Timer1);

        Advance(0x10);
        Assert.Equal(0xFFFE, IO.Read16(TM1CNT_L));
        Assert.Equal((ushort)Interrupt.Timer1, IO.Read16(IF) & (ushort)Interrupt.Timer1);

        StopTimers();
    }
}
