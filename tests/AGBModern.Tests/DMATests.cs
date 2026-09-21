namespace AGBModern.Tests;

public class DMATests
{
    private const uint DMA0SAD = 0x040000B0;
    private const uint DMA1SAD = 0x040000BC;
    private const uint DMA3SAD = 0x040000D4;
    private const uint WAITCNT = 0x04000204;

    private static void Start(uint registers, uint source, uint destination, ushort count, ushort control)
    {
        Memory.Write32(registers, source);
        Memory.Write32(registers + 4, destination);
        Memory.Write16(registers + 8, count);
        Memory.Write16(registers + 10, control);
    }

    [Fact]
    public void AnImmediateTransferRunsWhenEnabledAndThenDisablesItself()
    {
        Memory.Write32(0x02003000, 0x11111111);
        Memory.Write32(0x02003004, 0x22222222);

        Start(DMA3SAD, 0x02003000, 0x02003100, count: 2, control: 0x8400);

        Assert.Equal(0x11111111u, Memory.Read32(0x02003100));
        Assert.Equal(0x22222222u, Memory.Read32(0x02003104));
        Assert.Equal(0x0400, Memory.Read16(DMA3SAD + 10));
    }

    [Fact]
    public void AFixedSourceFills()
    {
        Memory.Write16(0x02003200, 0xABCD);

        Start(DMA3SAD, 0x02003200, 0x02003300, count: 3, control: 0x8100);

        Assert.Equal(0xABCD, Memory.Read16(0x02003300));
        Assert.Equal(0xABCD, Memory.Read16(0x02003304));
        Assert.Equal(0, Memory.Read16(0x02003306));
    }

    [Fact]
    public void ARepeatingHBlankTransferContinuesThroughItsSourceAndReloadsItsDestination()
    {
        for (uint i = 0; i < 4; i++)
        {
            Memory.Write16(0x02003400 + (i * 2), (ushort)(i + 1));
        }

        Start(DMA0SAD, 0x02003400, 0x02003500, count: 2, control: 0xA260);
        Assert.Equal(0, Memory.Read16(0x02003500));

        DMA.OnHBlank();
        Assert.Equal((1, 2), (Memory.Read16(0x02003500), Memory.Read16(0x02003502)));

        DMA.OnHBlank();
        Assert.Equal((3, 4), (Memory.Read16(0x02003500), Memory.Read16(0x02003502)));

        Memory.Write16(DMA0SAD + 10, 0);
    }

    [Fact]
    public void ASoundFIFOTransferTakesTwoNonSequentialAndSixSequentialAccessesAndTwoInternalCycles()
    {
        Memory.Write16(WAITCNT, 0);
        Start(DMA1SAD, 0x08000000, 0x040000A0, count: 0, control: 0xB600);

        long before = Scheduler.Cycles;
        DMA.OnSoundFIFO(0x040000A0);

        Assert.Equal((5 + 3) + (3 * (3 + 3)) + (4 * 1) + 2, Scheduler.Cycles - before);
        Memory.Write16(DMA1SAD + 10, 0);
    }

    [Fact]
    public void AHigherPriorityChannelRunsInsideALowerOnesTransfer()
    {
        const uint Block = 0x02003800;
        const uint Copy = 0x02003A00;
        const uint Marker = Block + (99 * 4);

        Memory.Write32(0x02003C00, 0x55555555);
        Start(DMA0SAD, 0x02003C00, Marker, count: 1, control: 0xA400);

        var hblank = Scheduler.CreateEvent(_ => DMA.OnHBlank());
        hblank.Schedule(20);
        Start(DMA3SAD, Block, Copy, count: 100, control: 0x8400);

        Assert.Equal(0x55555555u, Memory.Read32(Copy + (99 * 4)));
        Memory.Write16(DMA0SAD + 10, 0);
    }

    [Fact]
    public void VideoCaptureCopiesOnEachLineFromTwoAndStopsAt162()
    {
        Memory.Write16(0x02003600, 0x1234);
        Start(DMA3SAD, 0x02003600, 0x02003700, count: 1, control: 0xB300);

        DMA.OnLine(1);
        Assert.Equal(0, Memory.Read16(0x02003700));

        DMA.OnLine(2);
        DMA.OnLine(161);
        Assert.Equal((0x1234, 0x1234), (Memory.Read16(0x02003700), Memory.Read16(0x02003702)));

        DMA.OnLine(162);
        Assert.Equal(0, Memory.Read16(DMA3SAD + 10) & 0x8000);
    }
}
