namespace AGBModern;

/// <summary>The GBA's buttons. Combine them to hold several at once.</summary>
[Flags]
public enum Buttons : ushort
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    Select = 1 << 2,
    Start = 1 << 3,
    Right = 1 << 4,
    Left = 1 << 5,
    Up = 1 << 6,
    Down = 1 << 7,
    R = 1 << 8,
    L = 1 << 9,
}

/// <summary>The keypad, the way the game sees it.</summary>
public static class Keypad
{
    private const ushort AllButtons = 0x3FF;
    private const ushort IRQEnable = 1 << 14;
    private const ushort AllSelected = 1 << 15;

    /// <summary>The buttons being held right now. You can set it from any thread, like the one your input handling runs on.</summary>
    public static volatile Buttons Pressed;

    internal static ushort KEYINPUT => (ushort)(~(ushort)Pressed & AllButtons);

    internal static bool IsInterruptRequested
    {
        get
        {
            ushort keycnt = IO.Read16(IO.KEYCNT);
            var selected = (Buttons)(keycnt & AllButtons);
            var held = Pressed & selected;
            return (keycnt & IRQEnable) != 0 && ((keycnt & AllSelected) != 0 ? held == selected : held != Buttons.None);
        }
    }

    internal static void CheckInterrupt()
    {
        if (IsInterruptRequested)
        {
            Interrupts.Raise(Interrupt.Keypad);
        }
    }
}
