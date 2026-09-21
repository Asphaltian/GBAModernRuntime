namespace AGBModern.Tests;

public sealed class OpenBusTests : IDisposable
{
    private const uint Prefetched = 0x47704770;
    private const uint MemoryControl = 0x04000800;
    private const uint DMA3SAD = 0x040000D4;

    public void Dispose()
    {
        Memory.Write32(MemoryControl, IO.MemoryControlAtPowerOn);
        Memory.BIOSOpcode = 0;
    }

    [Fact]
    public void TheBIOSReadsAsItsLastFetchedOpcode()
    {
        Memory.BIOSOpcode = 0xE3A02004;

        Assert.Equal(0xE3A02004u, Memory.Read32(0x00000100, Prefetched));
        Assert.Equal(0xE3A0, Memory.Read16(0x00000102, Prefetched));
        Assert.Equal(0x20, Memory.Read8(0x00000001, Prefetched));
    }

    [Theory]
    [InlineData(0x00004000u)]
    [InlineData(0x10000000u)]
    [InlineData(0xFFFFFFFCu)]
    public void UnusedMemoryReadsAsThePrefetchedOpcode(uint address)
    {
        Assert.Equal(Prefetched, Memory.Read32(address, Prefetched));
        Assert.Equal(0x4770, Memory.Read16(address + 2, Prefetched));
    }

    [Fact]
    public void AnIOWordWithNothingReadableInItReadsAsThePrefetchedOpcode()
    {
        Assert.Equal(Prefetched, Memory.Read32(0x04000010, Prefetched));
        Assert.Equal(Prefetched, Memory.Read32(0x040000B0, Prefetched));
        Assert.Equal(Prefetched, Memory.Read32(0x04000400, Prefetched));
        Assert.Equal(0x47, Memory.Read8(0x0400004D, Prefetched));
    }

    [Fact]
    public void TheUnreadableHalfOfAnIOWordReadsAsZero()
    {
        Memory.Write16(0x04000134, 0);

        Assert.Equal(0, Memory.Read16(0x04000136, Prefetched));
        Assert.Equal(0, Memory.Read16(0x040000B8, Prefetched));
    }

    [Fact]
    public void Disabling256KWRAMMirrors32KWRAM()
    {
        Memory.Write32(0x03000010, 0x12345678);
        Memory.Write32(MemoryControl, IO.MemoryControlAtPowerOn & ~0x20u);

        Assert.Equal(0x12345678u, Memory.Read32(0x02000010, Prefetched));
        Memory.Write16(0x02008012, 0xABCD);
        Assert.Equal(0xABCD, Memory.Read16(0x03000012));
    }

    [Fact]
    public void DisabledWRAMReadsAsThePrefetchedOpcodeAndIgnoresWrites()
    {
        Memory.Write32(0x03000020, 0x11111111);
        Memory.Write32(MemoryControl, IO.MemoryControlAtPowerOn | 1);

        Assert.Equal(Prefetched, Memory.Read32(0x03000020, Prefetched));
        Assert.Equal(Prefetched, Memory.Read32(0x02000020, Prefetched));
        Memory.Write32(0x03000020, 0x22222222);

        Memory.Write32(MemoryControl, IO.MemoryControlAtPowerOn);
        Assert.Equal(0x11111111u, Memory.Read32(0x03000020));
    }

    [Fact]
    public void ADMAFromTheBIOSWritesTheLastValueItMovedAgain()
    {
        Memory.Write32(0x02000100, 0xCAFEBABE);
        Memory.Write32(DMA3SAD, 0x02000100);
        Memory.Write32(DMA3SAD + 4, 0x02000200);
        Memory.Write32(DMA3SAD + 8, 0x84000001);

        Memory.Write32(DMA3SAD, 0x00000000);
        Memory.Write32(DMA3SAD + 4, 0x02000300);
        Memory.Write32(DMA3SAD + 8, 0x84000001);

        Assert.Equal(0xCAFEBABEu, Memory.Read32(0x02000300));
    }
}
