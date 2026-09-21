namespace AGBModern.Tests;

public class IOTests
{
    [Theory]
    [InlineData(0x04000008u, 0xDFFF)]
    [InlineData(0x0400000Au, 0xDFFF)]
    [InlineData(0x0400000Cu, 0xFFFF)]
    [InlineData(0x04000048u, 0x3F3F)]
    [InlineData(0x0400004Au, 0x3F3F)]
    [InlineData(0x04000050u, 0x3FFF)]
    [InlineData(0x04000052u, 0x1F1F)]
    [InlineData(0x04000132u, 0xC3FF)]
    public void UnusedBitsReadAsZero(uint address, int readable)
    {
        Memory.Write16(address, 0xFFFF);

        Assert.Equal(readable, Memory.Read16(address));
        Memory.Write16(address, 0);
    }

    [Fact]
    public void POSTFLGOnlyKeepsItsFirstBit()
    {
        Memory.Write8(0x04000300, 0xFF);

        Assert.Equal(1, Memory.Read16(0x04000300));
        Memory.Write8(0x04000300, 0);
    }
}
