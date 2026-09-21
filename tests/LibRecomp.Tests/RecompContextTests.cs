namespace LibRecomp.Tests;

public class RecompContextTests
{
    private const uint IRQMode = 0x12;
    private const uint SystemMode = 0x1F;

    [Fact]
    public void StartsInSystemModeWithTheBIOSStackPointers()
    {
        var ctx = new RecompContext();
        Assert.Equal(SystemMode, ctx.CPSR);
        Assert.Equal(0x03007F00u, ctx.R13);

        ctx.WriteCPSR(IRQMode, 0b0001);
        Assert.Equal(0x03007FA0u, ctx.R13);
    }

    [Fact]
    public void EachModeKeepsItsOwnStackPointerAndLinkRegister()
    {
        var ctx = new RecompContext { R13 = 0x1000, R14 = 0x2000 };

        ctx.WriteCPSR(IRQMode, 0b0001);
        ctx.R13 = 0x3000;
        ctx.R14 = 0x4000;

        ctx.WriteCPSR(SystemMode, 0b0001);
        Assert.Equal(0x1000u, ctx.R13);
        Assert.Equal(0x2000u, ctx.R14);

        ctx.WriteCPSR(IRQMode, 0b0001);
        Assert.Equal(0x3000u, ctx.R13);
        Assert.Equal(0x4000u, ctx.R14);
    }

    [Fact]
    public void TheFieldMaskSelectsWhatAWriteChanges()
    {
        var ctx = new RecompContext();

        ctx.WriteCPSR(0xF0000000 | IRQMode, 0b1000);
        Assert.True(ctx.N && ctx.Z && ctx.C && ctx.V);
        Assert.Equal(0xF0000000 | SystemMode, ctx.CPSR);

        ctx.WriteCPSR(IRQMode, 0b0001);
        Assert.Equal(0xF0000000 | IRQMode, ctx.CPSR);
    }

    [Fact]
    public void AnExceptionSavesTheCPSRAndMasksIRQs()
    {
        var ctx = new RecompContext { Z = true };
        Assert.True(ctx.AreIRQsEnabled);

        ctx.EnterException(IRQMode);
        Assert.False(ctx.AreIRQsEnabled);
        Assert.Equal(0x40000000 | SystemMode, ctx.SPSR);

        ctx.Z = false;
        ctx.ReturnFromException();
        Assert.True(ctx.AreIRQsEnabled);
        Assert.True(ctx.Z);
        Assert.Equal(0x40000000 | SystemMode, ctx.CPSR);
    }

    [Fact]
    public void UserModeCanOnlyChangeTheFlags()
    {
        var ctx = new RecompContext();
        ctx.WriteCPSR(0x10, 0b1001);

        ctx.WriteCPSR(0x80000000 | SystemMode, 0b1001);

        Assert.Equal(0x80000000 | 0x10u, ctx.CPSR);
    }

    [Fact]
    public void FIQModeHasItsOwnR8ToR12()
    {
        var ctx = new RecompContext { R8 = 8, R12 = 12 };

        ctx.WriteCPSR(0x11, 0b0001);
        Assert.Equal((0u, 0u), (ctx.R8, ctx.R12));
        ctx.R8 = 80;

        ctx.WriteCPSR(SystemMode, 0b0001);
        Assert.Equal((8u, 12u), (ctx.R8, ctx.R12));
    }
}
