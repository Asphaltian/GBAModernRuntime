namespace AGBModern.Tests;

public class SerialTests
{
    private const uint SIODATA32 = 0x04000120;
    private const uint SIOMULTI0 = 0x04000120;
    private const uint SIOCNT = 0x04000128;
    private const uint SIODATA8 = 0x0400012A;
    private const uint RCNT = 0x04000134;
    private const uint JOY_RECV = 0x04000150;
    private const uint JOY_TRANS = 0x04000154;
    private const uint JOYSTAT = 0x04000158;
    private const uint IF = 0x04000202;

    public SerialTests()
    {
        Memory.Write16(RCNT, 0);
        Memory.Write16(SIOCNT, 0);
        Memory.Write16(IF, 0xFFFF);
    }

    private static void Wait(long cycles)
    {
        Scheduler.Cycles += cycles;
        Scheduler.RunDueEvents();
    }

    [Fact]
    public void AnInternalClockTransferWithNothingConnectedShiftsInOnes()
    {
        Memory.Write16(SIODATA8, 0x5A);
        Memory.Write16(SIOCNT, 0x4081);

        Wait(7 * 64);
        Assert.Equal(0x80, Memory.Read16(SIOCNT) & 0x80);
        Assert.Equal(0x7F, Memory.Read16(SIODATA8) & 0xFF);

        Wait(64);
        Assert.Equal(0, Memory.Read16(SIOCNT) & 0x80);
        Assert.Equal(0xFF, Memory.Read16(SIODATA8) & 0xFF);
        Assert.Equal((ushort)Interrupt.Serial, Memory.Read16(IF) & (ushort)Interrupt.Serial);
    }

    [Fact]
    public void A32BitTransferAt2MHzTakes32FastBits()
    {
        Memory.Write32(SIODATA32, 0x12345678);
        Memory.Write16(SIOCNT, 0x1000);
        Memory.Write16(SIOCNT, 0x1083);

        Wait(32 * 8);

        Assert.Equal(0xFFFFFFFFu, Memory.Read32(SIODATA32));
        Assert.Equal(0, Memory.Read16(SIOCNT) & 0x80);
    }

    [Fact]
    public void AnExternalClockTransferWaitsForAClockThatNeverComes()
    {
        Memory.Write16(SIODATA8, 0x5A);
        Memory.Write16(SIOCNT, 0x0080);

        Wait(100000);

        Assert.Equal(0x84, Memory.Read16(SIOCNT) & 0x84);
        Assert.Equal(0x5A, Memory.Read16(SIODATA8));
    }

    [Fact]
    public void ALoneGBAInMultiplayerModeIsAChildThatCannotStart()
    {
        Memory.Write16(SIOMULTI0, 0x1111);
        Memory.Write16(SIOCNT, 0x2000);
        Memory.Write16(SIOCNT, 0x2083);

        Wait(100000);

        Assert.Equal(0x200F, Memory.Read16(SIOCNT));
        Assert.Equal(0x1111, Memory.Read16(SIOMULTI0));
    }

    [Fact]
    public void GeneralPurposeInputsArePulledHigh()
    {
        Memory.Write16(RCNT, 0x8000 | 0x0090 | 0x0000);

        Assert.Equal(0x6, Memory.Read16(RCNT) & 0xF);

        Memory.Write16(RCNT, 0x8000 | 0x0090 | 0x0009);
        Assert.Equal(0xF, Memory.Read16(RCNT) & 0xF);
    }

    [Fact]
    public void JOYBusStatusFollowsTheDataRegisters()
    {
        Memory.Write16(RCNT, 0xC000);

        Memory.Write16(JOY_TRANS, 0x1234);
        Assert.Equal(0x2, Memory.Read16(JOYSTAT) & 0x2);

        Memory.Write16(JOYSTAT, 0x0038);
        Assert.Equal(0x32, Memory.Read16(JOYSTAT));

        Memory.Read16(JOY_RECV);
        Assert.Equal(0x32, Memory.Read16(JOYSTAT));
        Assert.Equal(0x0, Memory.Read16(RCNT) & 0x3);
    }

    [Fact]
    public void AUARTByteSentBlindlyLeavesTheSendRegister()
    {
        Memory.Write16(SIOCNT, 0x3000);
        Memory.Write16(SIOCNT, 0x3480);

        Memory.Write16(SIODATA8, 0x41);
        Assert.Equal(0x30, Memory.Read16(SIOCNT) & 0x30);

        Wait(10L * 16777216 / 9600);
        Assert.Equal(0x20, Memory.Read16(SIOCNT) & 0x30);
    }

    [Fact]
    public void AUARTByteWaitingForClearToSendStaysInTheSendRegister()
    {
        Memory.Write16(SIOCNT, 0x3000);
        Memory.Write16(SIOCNT, 0x3484);

        Memory.Write16(SIODATA8, 0x41);
        Wait(100000);

        Assert.Equal(0x30, Memory.Read16(SIOCNT) & 0x30);
    }
}
