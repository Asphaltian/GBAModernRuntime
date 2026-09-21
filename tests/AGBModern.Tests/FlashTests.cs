namespace AGBModern.Tests;

public class FlashTests
{
    public FlashTests() => SaveMemory.Initialize(SaveType.Flash128K);

    private const uint Base = 0x0E000000;

    private static void Command(byte command)
    {
        Memory.Write8(Base + 0x5555, 0xAA);
        Memory.Write8(Base + 0x2AAA, 0x55);
        Memory.Write8(Base + 0x5555, command);
    }

    private static void Program(uint address, byte value)
    {
        Command(0xA0);
        Memory.Write8(address, value);
    }

    [Fact]
    public void IDModeReportsAMacronixChip()
    {
        Command(0x90);
        Assert.Equal((0xC2, 0x09), (Memory.Read8(Base), Memory.Read8(Base + 1)));

        Command(0xF0);
        Assert.Equal(0xFF, Memory.Read8(Base + 1));
    }

    [Fact]
    public void ProgrammingClearsBitsAndErasingASectorSetsThemAgain()
    {
        Program(Base + 0x3234, 0xF0);
        Program(Base + 0x3234, 0x3C);
        Program(Base + 0x4000, 0x00);
        Assert.Equal(0x30, Memory.Read8(Base + 0x3234));
        Assert.True(SaveMemory.IsModified);

        Command(0x80);
        Memory.Write8(Base + 0x5555, 0xAA);
        Memory.Write8(Base + 0x2AAA, 0x55);
        Memory.Write8(Base + 0x3000, 0x30);

        Assert.Equal(0xFF, Memory.Read8(Base + 0x3234));
        Assert.Equal(0x00, Memory.Read8(Base + 0x4000));
    }

    [Fact]
    public void TheBankCommandSelectsTheOtherHalf()
    {
        Program(Base + 0x0100, 0x11);

        Command(0xB0);
        Memory.Write8(Base, 1);
        Assert.Equal(0xFF, Memory.Read8(Base + 0x0100));
        Program(Base + 0x0100, 0x22);

        Command(0xB0);
        Memory.Write8(Base, 0);
        Assert.Equal(0x11, Memory.Read8(Base + 0x0100));
        Assert.Equal(0x22, SaveMemory.Data[0x10100]);
    }
}
