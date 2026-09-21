namespace AGBModern.Tests;

public class SaveMemoryTests
{
    private const uint SRAM = 0x0E000000;
    private const uint EEPROM = 0x0D000000;
    private const uint Buffer = 0x02010000;
    private const uint DMA3SAD = 0x040000D4;

    private static void Transfer(uint source, uint destination, int count)
    {
        Memory.Write32(DMA3SAD, source);
        Memory.Write32(DMA3SAD + 4, destination);
        Memory.Write16(DMA3SAD + 8, (ushort)count);
        Memory.Write16(DMA3SAD + 10, 0x8000);
    }

    private static void SendBits(IEnumerable<int> bits)
    {
        int count = 0;
        foreach (int bit in bits)
        {
            Memory.Write16(Buffer + (uint)(count++ * 2), (ushort)bit);
        }

        Transfer(Buffer, EEPROM, count);
    }

    private static IEnumerable<int> Bits(ulong value, int count) => Enumerable.Range(0, count).Select(i => (int)((value >> (count - 1 - i)) & 1));

    [Fact]
    public void SRAMIsReadAndWrittenByteByByte()
    {
        SaveMemory.Initialize(SaveType.SRAM);

        Memory.Write8(SRAM + 0x1234, 0x5A);

        Assert.Equal(0x5A, Memory.Read8(SRAM + 0x1234));
        Assert.Equal(0x5A, SaveMemory.Data[0x1234]);
        Assert.True(SaveMemory.IsModified);
    }

    [Fact]
    public void SRAMIsMirroredAndWiderAccessesUseOneByte()
    {
        SaveMemory.Initialize(SaveType.SRAM);

        Memory.Write8(SRAM + 0x8000, 0x5A);
        Memory.Write32(0x0F010002, 0x11223344);

        Assert.Equal(0x5A, Memory.Read8(0x0FFF0000));
        Assert.Equal(0x5A5A, Memory.Read16(SRAM));
        Assert.Equal(0x22222222u, Memory.Read32(SRAM + 2));
    }

    [Theory]
    [InlineData(SaveType.EEPROM512, 6, 0x2A)]
    [InlineData(SaveType.EEPROM8K, 14, 0x3AB)]
    public void EEPROMWritesAndReadsA64BitBlock(SaveType type, int addressBits, int block)
    {
        Memory.LoadROM(new byte[0x200]);
        SaveMemory.Initialize(type);
        const ulong Data = 0x0123456789ABCDEF;

        SendBits([1, 0, .. Bits((ulong)block, addressBits), .. Bits(Data, 64), 0]);
        Assert.Equal(0, Memory.Read16(EEPROM) & 1);
        Scheduler.Cycles += 108368;
        Assert.Equal(1, Memory.Read16(EEPROM) & 1);

        SendBits([1, 1, .. Bits((ulong)block, addressBits), 0]);
        Transfer(EEPROM, Buffer, 68);

        ulong read = 0;
        for (uint i = 4; i < 68; i++)
        {
            read = (read << 1) | (Memory.Read16(Buffer + (i * 2)) & 1u);
        }

        Assert.Equal(Data, read);
        Assert.Equal(0x01, SaveMemory.Data[block * 8]);
        Assert.Equal(0xEF, SaveMemory.Data[(block * 8) + 7]);
    }

    [Fact]
    public void EEPROMIsOnlyAtTheTopOfTheRegionWithALargeROM()
    {
        Memory.LoadROM(new byte[0x1000002]);
        SaveMemory.Initialize(SaveType.EEPROM512);

        Memory.Write16(EEPROM, 1);
        Memory.Write16(EEPROM, 1);

        Assert.Equal(1, Memory.Read16(0x0DFFFF00));
        Assert.Equal(0x0000, Memory.Read16(EEPROM));
    }

    [Fact]
    public void SmallFlashIsAPanasonicChipWithoutBanks()
    {
        SaveMemory.Initialize(SaveType.Flash64K);

        Command(0x90);
        Assert.Equal((0x32, 0x1B), (Memory.Read8(SRAM), Memory.Read8(SRAM + 1)));
        Command(0xF0);

        Command(0xB0);
        Memory.Write8(SRAM, 1);
        Command(0xA0);
        Memory.Write8(SRAM + 0x100, 0x11);

        Assert.Equal(0x11, SaveMemory.Data[0x100]);
        Assert.Equal(0x10000, SaveMemory.Data.Length);
    }

    [Fact]
    public void ALoadedImageOfAnotherSizeIsCutOrPadded()
    {
        SaveMemory.Initialize(SaveType.EEPROM512);

        SaveMemory.Load(Enumerable.Repeat((byte)0x12, 0x2000).ToArray());
        Assert.Equal(0x200, SaveMemory.Data.Length);

        SaveMemory.Load([0x34]);
        Assert.Equal([0x34, 0xFF], SaveMemory.Data[..2].ToArray());
    }

    private static void Command(byte command)
    {
        Memory.Write8(SRAM + 0x5555, 0xAA);
        Memory.Write8(SRAM + 0x2AAA, 0x55);
        Memory.Write8(SRAM + 0x5555, command);
    }
}
