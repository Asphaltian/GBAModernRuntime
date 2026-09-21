namespace LibRecomp.Tests;

public class ShifterTests
{
    [Theory]
    [InlineData(0x80000001, 0, 0x80000001)]
    [InlineData(0x80000001, 1, 0x00000002)]
    [InlineData(0x80000001, 31, 0x80000000)]
    [InlineData(0x80000001, 32, 0)]
    [InlineData(0x80000001, 255, 0)]
    public void LSL(uint value, uint amount, uint expected)
    {
        Assert.Equal(expected, Shifter.LSL(value, amount));
    }

    [Theory]
    [InlineData(0x80000001, 0, 0x80000001)]
    [InlineData(0x80000001, 1, 0x40000000)]
    [InlineData(0x80000001, 31, 1)]
    [InlineData(0x80000001, 32, 0)]
    [InlineData(0x80000001, 255, 0)]
    public void LSR(uint value, uint amount, uint expected)
    {
        Assert.Equal(expected, Shifter.LSR(value, amount));
    }

    [Theory]
    [InlineData(0x80000000, 0, 0x80000000)]
    [InlineData(0x80000000, 4, 0xF8000000)]
    [InlineData(0x80000000, 32, 0xFFFFFFFF)]
    [InlineData(0x80000000, 255, 0xFFFFFFFF)]
    [InlineData(0x40000000, 32, 0)]
    public void ASR(uint value, uint amount, uint expected)
    {
        Assert.Equal(expected, Shifter.ASR(value, amount));
    }

    [Theory]
    [InlineData(0x00000003, 0, 0x00000003)]
    [InlineData(0x00000003, 1, 0x80000001)]
    [InlineData(0x00000003, 32, 0x00000003)]
    [InlineData(0x00000003, 33, 0x80000001)]
    public void ROR(uint value, uint amount, uint expected)
    {
        Assert.Equal(expected, Shifter.ROR(value, amount));
    }

    [Fact]
    public void AnAmountOfZeroPassesTheCarryThrough()
    {
        Assert.True(Shifter.LSLCarry(0, 0, true));
        Assert.True(Shifter.LSRCarry(0, 0, true));
        Assert.True(Shifter.ASRCarry(0, 0, true));
        Assert.True(Shifter.RORCarry(0, 0, true));
        Assert.False(Shifter.LSLCarry(0xFFFFFFFF, 0, false));
    }

    [Fact]
    public void TheCarryIsTheLastBitShiftedOut()
    {
        Assert.True(Shifter.LSLCarry(0x80000000, 1, false));
        Assert.True(Shifter.LSLCarry(0x00000001, 32, false));
        Assert.False(Shifter.LSLCarry(0xFFFFFFFF, 33, true));

        Assert.True(Shifter.LSRCarry(0x00000001, 1, false));
        Assert.True(Shifter.LSRCarry(0x80000000, 32, false));
        Assert.False(Shifter.LSRCarry(0xFFFFFFFF, 33, true));

        Assert.True(Shifter.ASRCarry(0x00000004, 3, false));
        Assert.True(Shifter.ASRCarry(0x80000000, 32, false));
        Assert.True(Shifter.ASRCarry(0x80000000, 200, false));
        Assert.False(Shifter.ASRCarry(0x7FFFFFFF, 32, true));

        Assert.True(Shifter.RORCarry(0x00000001, 1, false));
        Assert.True(Shifter.RORCarry(0x80000000, 32, false));
        Assert.False(Shifter.RORCarry(0x7FFFFFFF, 64, true));
    }
}
