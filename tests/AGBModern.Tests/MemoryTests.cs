namespace AGBModern.Tests;

public class MemoryTests
{
    private readonly record struct Peeked(short Low, short High, int Next);

    [Fact]
    public void WorkRAMIsMirroredThroughItsRegion()
    {
        Memory.Write32(0x02000010, 0x11223344);
        Assert.Equal(0x11223344u, Memory.Read32(0x02040010));
        Assert.Equal(0x11223344u, Memory.Read32(0x02FC0010));

        Memory.Write32(0x03007FFC, 0x55667788);
        Assert.Equal(0x55667788u, Memory.Read32(0x03FFFFFC));
    }

    [Fact]
    public void ValuesAreLittleEndian()
    {
        Memory.Write32(0x02000020, 0xAABBCCDD);
        Assert.Equal(0xDD, Memory.Read8(0x02000020));
        Assert.Equal(0xAA, Memory.Read8(0x02000023));
        Assert.Equal(0xCCDD, Memory.Read16(0x02000020));
        Assert.Equal(0xAABB, Memory.Read16(0x02000022));
    }

    [Fact]
    public void WordAndHalfwordAccessesAreAligned()
    {
        Memory.Write32(0x02000030, 0x01020304);
        Assert.Equal(0x01020304u, Memory.Read32(0x02000033));
        Assert.Equal(0x0304, Memory.Read16(0x02000031));
    }

    [Fact]
    public void TheLastQuarterOfVRAMSpaceMirrorsTheObjectTiles()
    {
        Memory.Write16(0x06010000, 0x1234);
        Assert.Equal(0x1234, Memory.Read16(0x06018000));
        Assert.Equal(0x1234, Memory.Read16(0x06030000));
    }

    [Fact]
    public void ROMIsReadOnlyAndMirroredAtEachWaitStateRegion()
    {
        Memory.LoadROM([0x78, 0x56, 0x34, 0x12]);
        Memory.Write32(0x08000000, 0);

        Assert.Equal(0x12345678u, Memory.Read32(0x08000000));
        Assert.Equal(0x12345678u, Memory.Read32(0x0A000000));
        Assert.Equal(0x12345678u, Memory.Read32(0x0C000000));
    }

    [Fact]
    public void AddressesPastTheEndOfTheROMReadBackAsTheAddress()
    {
        Memory.LoadROM([0, 0, 0, 0]);

        Assert.Equal(0x0002, Memory.Read16(0x08000004));
        Assert.Equal(0x8000, Memory.Read16(0x08010000));
        Assert.Equal(0x00030002u, Memory.Read32(0x08000004));
    }

    [Theory]
    [InlineData(0x0000, 0x03000000, 1, 1)]
    [InlineData(0x0000, 0x02000000, 3, 6)]
    [InlineData(0x0000, 0x06000000, 1, 2)]
    [InlineData(0x0000, 0x08000000, 5, 8)]
    [InlineData(0x0000, 0x0C000000, 5, 14)]
    [InlineData(0x4317, 0x08000000, 4, 6)]
    [InlineData(0x4317, 0x0E000000, 9, 9)]
    public void AnAccessTakesAsLongAsWAITCNTSays(ushort waitcnt, uint address, int halfwordCycles, int wordCycles)
    {
        Memory.Write16(0x04000204, waitcnt);
        long before = Scheduler.Cycles;
        Memory.Read16(address);
        Assert.Equal(halfwordCycles, Scheduler.Cycles - before);

        before = Scheduler.Cycles;
        Memory.Read32(address);
        Assert.Equal(wordCycles, Scheduler.Cycles - before);
        Memory.Write16(0x04000204, 0);
    }

    [Fact]
    public void ByteWritesToVideoMemoryFillTheHalfwordOrAreIgnored()
    {
        Memory.Write16(0x05000010, 0);
        Memory.Write16(0x07000010, 0x1234);

        Memory.Write8(0x05000011, 0xAB);
        Memory.Write8(0x07000010, 0xCD);

        Assert.Equal(0xABAB, Memory.Read16(0x05000010));
        Assert.Equal(0x1234, Memory.Read16(0x07000010));
    }

    [Fact]
    public void WriteOnlyRegistersReadAsZero()
    {
        Memory.Write16(0x04000010, 0x0123);
        Memory.Write16(0x04000008, 0x0456);

        Assert.Equal(0, Memory.Read16(0x04000010));
        Assert.Equal(0x0456, Memory.Read16(0x04000008));
        Memory.Write16(0x04000008, 0);
    }

    [Fact]
    public void PeekReadsAStructureWithoutTakingCycles()
    {
        Memory.Write32(0x02000040, 0x00010002);
        Memory.Write32(0x02000044, 0xFFFFFFFE);

        long before = Scheduler.Cycles;
        var value = Memory.Peek<Peeked>(0x02000040);

        Assert.Equal(new Peeked(2, 1, -2), value);
        Assert.Equal(before, Scheduler.Cycles);
        Assert.Throws<ArgumentOutOfRangeException>(() => Memory.Peek<int>(0x04000000));
    }

    [Fact]
    public void PokeChangesRAMInPlaceAndRefusesAnythingElse()
    {
        Memory.Poke<Peeked>(0x03000100) = new Peeked(7, 8, 9);

        Assert.Equal(new Peeked(7, 8, 9), Memory.Peek<Peeked>(0x03000100));
        Assert.Equal(0x00080007u, Memory.Read32(0x03000100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Memory.Poke<int>(0x08000000));
    }

    [Fact]
    public void AllocatedExtendedRAMIsReadableByTheGame()
    {
        uint first = Memory.Allocate(5);
        uint second = Memory.Allocate(4);

        Assert.Equal(0u, first % 4);
        Assert.Equal(first + 8, second);

        Memory.Poke<uint>(second) = 0xCAFEF00D;
        Assert.Equal(0xCAFEF00Du, Memory.Read32(second));
        Assert.Equal(0x0Du, Memory.Read8(second));

        Memory.Write16(first, 0x1234);
        Assert.Equal((ushort)0x1234, Memory.Peek<ushort>(first));
    }

    [Fact]
    public void FreedExtendedRAMIsGivenOutAgainAndJoinsItsNeighbors()
    {
        uint first = Memory.Allocate(16);
        uint second = Memory.Allocate(16);
        uint third = Memory.Allocate(16);

        Memory.Free(first);
        Memory.Free(second);
        Assert.Equal(first, Memory.Allocate(32));

        Memory.Free(first);
        Memory.Free(third);
        Assert.Throws<ArgumentException>(() => Memory.Free(third));
    }

    [Fact]
    public void TheDebugPortsNameTheRuntimeAndPrintCharacters()
    {
        var output = new StringWriter();
        var console = Console.Out;
        Console.SetOut(output);

        Memory.Write8(0x04FFFA1C, (byte)'h');
        Memory.Write16(0x04FFFA1C, 'i');

        Console.SetOut(console);
        Assert.Equal("hi", output.ToString());
        Assert.Equal("GBAModernRuntime", string.Concat(Enumerable.Range(0, 16).Select(i => (char)Memory.Read8(0x04FFFA00 + (uint)i))));
    }

    [Fact]
    public void WritingOneToAnInterruptFlagClearsIt()
    {
        Interrupts.Raise(Interrupt.VBlank | Interrupt.Timer3);
        Assert.Equal((ushort)(Interrupt.VBlank | Interrupt.Timer3), Memory.Read16(0x04000202));

        Memory.Write16(0x04000202, (ushort)Interrupt.VBlank);
        Assert.Equal((ushort)Interrupt.Timer3, Memory.Read16(0x04000202));

        Memory.Write8(0x04000202, (byte)Interrupt.Timer3);
        Assert.Equal(0, Memory.Read16(0x04000202));
    }
}
