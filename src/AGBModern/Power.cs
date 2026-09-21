namespace AGBModern;

internal static class Power
{
    private const byte StopBit = 0x80;

    public static Action? WhileStopped { get; set; }

    public static void Halt()
    {
        while (!Interrupts.IsRequested)
        {
            if (!Scheduler.SkipToNextEvent())
            {
                throw new InvalidOperationException("The CPU halted with nothing scheduled that could wake it.");
            }

            Scheduler.RunDueEvents();
        }
    }

    public static void Stop()
    {
        while ((IO.Read16(IO.IE) & (ushort)Interrupt.Keypad) == 0 || !Keypad.IsInterruptRequested)
        {
            WhileStopped?.Invoke();
            Thread.Sleep(1);
        }
    }

    internal static void WriteHALTCNT(byte value)
    {
        if ((value & StopBit) != 0)
        {
            Stop();
        }
        else
        {
            Halt();
        }
    }
}
