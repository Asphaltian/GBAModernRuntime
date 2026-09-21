using AGBModern;

namespace LibRecomp.Tests;

public class LoadsTests
{
    [Fact]
    public void MisalignedWordsRotate()
    {
        Memory.Write32(0x03000200, 0x11223344);

        Assert.Equal(0x44112233u, Loads.Word(0x03000201));
    }

    [Fact]
    public void OddHalfwordsRotateAndSignedOnesReadAByte()
    {
        Memory.Write16(0x03000200, 0x80F0);

        Assert.Equal(0xF0000080u, Loads.Halfword(0x03000201));
        Assert.Equal(0xFFFFFF80u, Loads.SignedHalfword(0x03000201));
        Assert.Equal(0xFFFF80F0u, Loads.SignedHalfword(0x03000200));
    }
}
