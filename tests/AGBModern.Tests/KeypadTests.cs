namespace AGBModern.Tests;

public sealed class KeypadTests : IDisposable
{
    private const uint KEYCNT = 0x04000132;
    private const uint IF = 0x04000202;

    public KeypadTests() => Memory.Write16(IF, 0xFFFF);

    public void Dispose()
    {
        Keypad.Pressed = Buttons.None;
        Memory.Write16(KEYCNT, 0);
        Memory.Write16(IF, 0xFFFF);
    }

    private static bool IsRaised => (Memory.Read16(IF) & (ushort)Interrupt.Keypad) != 0;

    [Fact]
    public void AnyOfTheSelectedButtonsRequestsTheInterrupt()
    {
        Keypad.Pressed = Buttons.B;

        Memory.Write16(KEYCNT, 0x4000 | (ushort)(Buttons.A | Buttons.B));

        Assert.True(IsRaised);
    }

    [Fact]
    public void AllOfTheSelectedButtonsAreNeededInAndMode()
    {
        Keypad.Pressed = Buttons.A;
        Memory.Write16(KEYCNT, 0xC000 | (ushort)(Buttons.A | Buttons.B));
        Assert.False(IsRaised);

        Keypad.Pressed = Buttons.A | Buttons.B;
        Memory.Write16(KEYCNT, 0xC000 | (ushort)(Buttons.A | Buttons.B));
        Assert.True(IsRaised);
    }

    [Fact]
    public void NothingIsRequestedWhileTheInterruptIsDisabled()
    {
        Keypad.Pressed = Buttons.Start;

        Memory.Write16(KEYCNT, (ushort)Buttons.Start);

        Assert.False(IsRaised);
    }
}
