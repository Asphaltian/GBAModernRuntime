namespace AGBModern;

/// <summary>Everything that can interrupt the game. Each one has the same bit it has in IE and IF.</summary>
[Flags]
public enum Interrupt : ushort
{
    VBlank = 1 << 0,
    HBlank = 1 << 1,
    VCount = 1 << 2,
    Timer0 = 1 << 3,
    Timer1 = 1 << 4,
    Timer2 = 1 << 5,
    Timer3 = 1 << 6,
    Serial = 1 << 7,
    DMA0 = 1 << 8,
    DMA1 = 1 << 9,
    DMA2 = 1 << 10,
    DMA3 = 1 << 11,
    Keypad = 1 << 12,
    GamePak = 1 << 13,
}

/// <summary><c>Interrupts</c> lets you check and send the game's interrupts.</summary>
public static class Interrupts
{
    /// <summary>Returns <c>true</c> if an interrupt that the game turned on in IE is waiting in IF.</summary>
    public static bool IsRequested => (IO.Read16(IO.IE) & IO.Read16(IO.IF)) != 0;

    /// <summary>The same as <see cref="IsRequested"/>, but only while IME is on as well.</summary>
    public static bool IsPending => (IO.Read16(IO.IME) & 1) != 0 && IsRequested;

    /// <summary>Sends the game an interrupt, just like the hardware would.</summary>
    public static void Raise(Interrupt interrupt)
    {
        IO.SetInterruptFlags((ushort)interrupt);
    }
}
