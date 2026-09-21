using AGBModern;

namespace LibRecomp.Tests;

public class BIOSTests
{
    private const uint Source = 0x02020000;
    private const uint Destination = 0x02030000;
    private const uint VRAM = 0x06000000;

    private static RecompContext Call(int function, uint r0 = 0, uint r1 = 0, uint r2 = 0, uint r3 = 0)
    {
        var ctx = new RecompContext { R0 = r0, R1 = r1, R2 = r2, R3 = r3 };
        BIOS.Call(ctx, function);
        return ctx;
    }

    private static void Put(uint address, params byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            Memory.Write8(address + (uint)i, bytes[i]);
        }
    }

    private static byte[] Get(uint address, int length) => [.. Enumerable.Range(0, length).Select(i => Memory.Read8(address + (uint)i))];

    [Fact]
    public void DivReturnsTheQuotientTheRemainderAndTheQuotientsSize()
    {
        var ctx = Call(0x06, unchecked((uint)-1234), 10);

        Assert.Equal((-123, -4, 123u), ((int)ctx.R0, (int)ctx.R1, ctx.R3));
    }

    [Fact]
    public void DivArmTakesItsArgumentsTheOtherWayAround()
    {
        Assert.Equal(-123, (int)Call(0x07, 10, unchecked((uint)-1234)).R0);
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(2u, 1u)]
    [InlineData(2u << 30, 46340u)]
    [InlineData(uint.MaxValue, 65535u)]
    public void SqrtRoundsDown(uint value, uint root)
    {
        Assert.Equal(root, Call(0x08, value).R0);
    }

    [Fact]
    public void ArcTanCoversTheHalfTurnAroundZero()
    {
        var ctx = Call(0x09, 0x4000);
        Assert.Equal((0x2000u, 0xFFFFC000u, 0x8000u), (ctx.R0, ctx.R1, ctx.R3));
        Assert.Equal(0xFFFFE000u, Call(0x09, 0xFFFFC000).R0);
        Assert.Equal(0x12E4u, Call(0x09, 0x2000).R0);
    }

    [Fact]
    public void ArcTan2CoversTheWholeTurn()
    {
        Assert.Equal(0x0000u, Call(0x0A, 0x4000, 0).R0);
        Assert.Equal(0x4000u, Call(0x0A, 0, 0x4000).R0);
        Assert.Equal(0xC000u, Call(0x0A, 0, 0xFFFFC000).R0);
        Assert.Equal(0x2000u, Call(0x0A, 0x4000, 0x4000).R0);
        Assert.Equal(0x6000u, Call(0x0A, 0xFFFFC000, 0x4000).R0);
        Assert.Equal(0xA000u, Call(0x0A, 0xFFFFC000, 0xFFFFC000).R0);
        Assert.Equal((0xE000u, 0x170u), (Call(0x0A, 0x4000, 0xFFFFC000).R0, Call(0x0A, 0x4000, 0xFFFFC000).R3));
    }

    [Fact]
    public void CpuSetRefusesToReadTheBIOS()
    {
        Memory.Write32(Destination, 0x12345678);

        Call(0x0B, 0x00000100, Destination, 1 | (1 << 26));

        Assert.Equal(0x12345678u, Memory.Read32(Destination));
    }

    [Fact]
    public void BitUnPackWidensEachUnitAndAddsTheOffsetToNonZeroOnes()
    {
        Put(Source, 0b1011_0100);
        uint info = Source + 0x10;
        Memory.Write16(info, 1);
        Put(info + 2, 1, 4);
        Memory.Write32(info + 4, 2);

        Call(0x10, Source, Destination, info);

        Assert.Equal(0x30330300u, Memory.Read32(Destination));
    }

    [Fact]
    public void LZ77CopiesBackReferences()
    {
        Put(Source, 0x10, 8, 0, 0, 0b0100_0000, (byte)'a', 0x40, 0x00);

        Call(0x11, Source, Destination);

        Assert.Equal("aaaaaaaa"u8.ToArray(), Get(Destination, 8));
    }

    [Fact]
    public void LZ77InHalfwordsWritesPairs()
    {
        Put(Source, 0x10, 6, 0, 0, 0b0010_0000, (byte)'a', (byte)'b', 0x10, 0x01);

        Call(0x12, Source, VRAM);

        Assert.Equal("ababab"u8.ToArray(), Get(VRAM, 6));
    }

    [Fact]
    public void RunLengthRepeatsOrCopies()
    {
        Put(Source, 0x30, 6, 0, 0, 0x81, (byte)'x', 0x01, (byte)'y', (byte)'z');

        Call(0x14, Source, Destination);

        Assert.Equal("xxxxyz"u8.ToArray(), Get(Destination, 6));
    }

    [Fact]
    public void HuffmanFollowsTheTreeFromTheRoot()
    {
        Put(Source, 0x28, 4, 0, 0, 0x03, 0x80, (byte)'f', 0xC0, (byte)'H', (byte)'u', 0, 0);
        Memory.Write32(Source + 12, 0b10_11_0_0u << 26);

        Call(0x13, Source, Destination);

        Assert.Equal("Huff"u8.ToArray(), Get(Destination, 4));
    }

    [Fact]
    public void HardwareKeepsRunningWhileTheBIOSDecompresses()
    {
        const int Delay = 50;
        Put(Source, 0x30, 0, 1, 0, 0xFF, (byte)'x', 0xFB, (byte)'x');
        long start = Scheduler.Cycles;
        long? ranAt = null;
        Scheduler.CreateEvent(_ => ranAt = Scheduler.Cycles).Schedule(Delay);

        Call(0x14, Source, Destination);

        Assert.InRange(ranAt!.Value, start + Delay, Scheduler.Cycles - 1);
    }

    [Fact]
    public void DiffUnFiltersAddUpTheDifferences()
    {
        Put(Source, 0x81, 4, 0, 0, 10, 1, 1, 0xFF);
        Call(0x16, Source, Destination);
        Assert.Equal([10, 11, 12, 11], Get(Destination, 4));

        Put(Source, 0x82, 4, 0, 0, 0x00, 0x01, 0x01, 0x00);
        Call(0x18, Source, Destination);
        Assert.Equal((ushort)0x0101, Memory.Read16(Destination + 2));
    }

    [Fact]
    public void SoftResetClearsTheTopOfWorkRAMAndStartsTheROMAgain()
    {
        Memory.Write32(0x03007FFC, 0xCAFEF00D);
        Memory.Write8(0x03007FFA, 0);

        var reset = Assert.Throws<BIOS.GameReset>(() => Call(0x00));

        Assert.Equal(0x08000000u, reset.Start);
        Assert.Equal(0u, Memory.Read32(0x03007FFC));
    }

    [Fact]
    public void RegisterRamResetBlanksTheScreen()
    {
        Memory.Write32(Destination, 1);

        Call(0x01, 1);

        Assert.Equal(0u, Memory.Read32(Destination));
        Assert.Equal(0x0080, Memory.Read16(0x04000000));
    }

    [Fact]
    public void RegisterRamResetLeavesTheRotationParametersUnscaled()
    {
        Memory.Write16(0x04000020, 0x1234);

        Call(0x01, 0x80);

        Assert.Equal((0x100, 0, 0x100), (IO.ReadRegister(0x04000020), IO.ReadRegister(0x04000022), IO.ReadRegister(0x04000036)));
    }

    [Fact]
    public void HardResetClearsAllOfWorkRAMAndStartsTheROMAgain()
    {
        Memory.Write32(0x03007FFC, 0xCAFEF00D);
        Memory.Write8(0x03007FFA, 1);

        var reset = Assert.Throws<BIOS.GameReset>(() => Call(0x26));

        Assert.Equal(0x08000000u, reset.Start);
        Assert.Equal(0u, Memory.Read32(0x03007FFC));
    }

    [Theory]
    [InlineData(0x0000, 0x0100, 0x0000, 0x0000, 0x0100)]
    [InlineData(0x4000, 0x0000, 0xFF00, 0x0100, 0x0000)]
    [InlineData(0x80FF, 0xFF00, 0x0000, 0x0000, 0xFF00)]
    public void ObjAffineSetRotatesByTheUpperByteOfTheAngle(ushort angle, ushort pa, ushort pb, ushort pc, ushort pd)
    {
        Memory.Write16(Source, 0x100);
        Memory.Write16(Source + 2, 0x100);
        Memory.Write16(Source + 4, angle);

        Call(0x0F, Source, Destination, 1, 8);

        Assert.Equal((pa, pb, pc, pd), (Memory.Read16(Destination), Memory.Read16(Destination + 8), Memory.Read16(Destination + 16), Memory.Read16(Destination + 24)));
    }

    [Fact]
    public void BgAffineSetPutsTheOriginAtTheDisplayCenter()
    {
        Memory.Write32(Source, 64 << 8);
        Memory.Write32(Source + 4, 32 << 8);
        Memory.Write16(Source + 8, 120);
        Memory.Write16(Source + 10, 80);
        Memory.Write16(Source + 12, 0x200);
        Memory.Write16(Source + 14, 0x100);
        Memory.Write16(Source + 16, 0);

        Call(0x0E, Source, Destination, 1);

        Assert.Equal((ushort)0x200, Memory.Read16(Destination));
        Assert.Equal((64 - (2 * 120)) << 8, (int)Memory.Read32(Destination + 8));
        Assert.Equal((32 - 80) << 8, (int)Memory.Read32(Destination + 12));
    }

    [Fact]
    public void AfterACallTheBIOSReadsAsTheOpcodeAfterTheSWI()
    {
        Call(0x06, 10, 2);

        Assert.Equal(0xE3A02004u, Memory.Read32(0x00000000));
    }

    [Fact]
    public void AnInterruptRunsTheHandlerInIRQModeAndPutsEverythingBack()
    {
        const uint Handler = 0x08000200;
        (uint R0, uint Mode)? seen = null;
        Recomp.RegisterFunctions([(Handler, handlerCtx => seen = (handlerCtx.R0, handlerCtx.CPSR & 0x1F))]);
        Memory.Write32(0x03007FFC, Handler);
        Memory.Write16(0x04000200, (ushort)Interrupt.VBlank);
        Memory.Write16(0x04000208, 1);
        Interrupts.Raise(Interrupt.VBlank);

        var ctx = new RecompContext { R0 = 1, R1 = 2, R12 = 3, R14 = 4 };
        Recomp.HandleEvents(ctx);

        Assert.Equal((0x04000000u, 0x12u), seen);
        Assert.Equal((1u, 2u, 3u, 4u, 0x1Fu), (ctx.R0, ctx.R1, ctx.R12, ctx.R14, ctx.CPSR & 0x1F));
        Memory.Write16(0x04000202, 0xFFFF);
        Memory.Write16(0x04000208, 0);
    }

    [Fact]
    public void NoInterruptIsTakenWhileTheCPUHasThemOff()
    {
        bool called = false;
        Recomp.RegisterFunctions([(0x08000300, _ => called = true)]);
        Memory.Write32(0x03007FFC, 0x08000300);
        Memory.Write16(0x04000200, (ushort)Interrupt.VBlank);
        Memory.Write16(0x04000208, 1);
        Interrupts.Raise(Interrupt.VBlank);

        var ctx = new RecompContext();
        ctx.WriteCPSR(0x9F, 0b0001);
        Recomp.HandleEvents(ctx);

        Assert.False(called);
        Memory.Write16(0x04000202, 0xFFFF);
        Memory.Write16(0x04000208, 0);
    }

    [Fact]
    public void TheSoundDriverCallsSayWhichTheyAre()
    {
        var error = Assert.Throws<NotSupportedException>(() => Call(0x1A));

        Assert.Contains("0x1A", error.Message);
    }
}
