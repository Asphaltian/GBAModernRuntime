using AGBModern;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LibRecomp.Tests;

public class CPUTimingTests
{
    private const uint WAITCNT = 0x04000204;

    [Theory]
    [InlineData(0x08000100u, true, 5 + 3)]
    [InlineData(0x08000100u, false, (5 + 3) + (3 + 3))]
    [InlineData(0x02000100u, true, 3 + 3)]
    [InlineData(0x02000100u, false, 6 + 6)]
    [InlineData(0x03000100u, false, 1 + 1)]
    [InlineData(0x06000100u, false, 2 + 2)]
    public void AJumpCostsTwoFetchesWhereItLands(uint target, bool isThumb, int cycles)
    {
        Memory.Write16(WAITCNT, 0);

        Assert.Equal(cycles, CPUTiming.JumpCycles(target, isThumb));
    }

    [Fact]
    public void CodeCopiedToRAMFetchesAtThatRAMsSpeed()
    {
        const uint Original = 0x08000200;
        const uint Copy = 0x02000200;
        byte[] code = [0x70, 0x47];
        code.CopyTo(Memory.ROM, (int)(Original & 0x1FFFFFF));
        code.CopyTo(Memory.EWRAM, (int)(Copy & 0x3FFFF));

        int? cycles = null;
        Recomp.RegisterRAMFunctions([(Original, (uint)code.Length, _ => cycles = CPUTiming.CopiedToRAMFetch(halfwords: 4, accesses: 2))]);

        Recomp.LookupFunc(Copy)(new RecompContext());

        Assert.Equal(4 * 3, cycles);
        Assert.False(Recomp.IsCopyInEWRAM);
    }
}
