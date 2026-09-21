namespace AGBModern;

/// <summary>The GBA's display. Subscribe to <see cref="FrameFinished"/> to get each frame as soon as it's done.</summary>
public static class Video
{
    public const int VisibleLines = 160;
    public const int TotalLines = 228;

    /// <summary>How many cycles one frame takes.</summary>
    public const int CyclesPerFrame = TotalLines * CyclesPerLine;

    private const int CyclesPerLine = 1232;
    private const int CyclesUntilHBlank = 1006;

    private const ushort VBlankFlag = 1 << 0;
    private const ushort HBlankFlag = 1 << 1;
    private const ushort VCountFlag = 1 << 2;
    private const ushort VBlankIRQEnable = 1 << 3;
    private const ushort HBlankIRQEnable = 1 << 4;
    private const ushort VCountIRQEnable = 1 << 5;

    private static readonly Scheduler.Event HBlankEvent = Scheduler.CreateEvent(StartHBlank);
    private static readonly Scheduler.Event LineEvent = Scheduler.CreateEvent(StartLine);

    private static readonly uint[] ReferencePointRegisters = [0x28, 0x2C, 0x38, 0x3C]; // BG2X, BG2Y, BG3X, BG3Y
    private static readonly uint[] ReferencePointSteps = [0x22, 0x26, 0x32, 0x36];     // BG2PB, BG2PD, BG3PB, BG3PD

    private static readonly VideoFrame Frame = new();
    private static readonly int[] ReferencePoints = new int[4];

    private static FramePacer? _pacer;
    private static ushort _dispstat;
    private static int _line;
    private static bool _inHBlank;

    /// <summary>
    /// Gives you each frame once its last visible line is drawn. This runs on the game's thread, and
    /// you get the same frame object every time, so copy what you need before you return.
    /// </summary>
    public static event Action<VideoFrame>? FrameFinished;

    internal static ushort VCOUNT => (ushort)_line;

    internal static ushort DISPSTAT
    {
        get
        {
            int flags = _dispstat;
            if (_line is >= VisibleLines and < TotalLines - 1)
            {
                flags |= VBlankFlag;
            }

            if (_inHBlank)
            {
                flags |= HBlankFlag;
            }

            if (_line == _dispstat >> 8)
            {
                flags |= VCountFlag;
            }

            return (ushort)flags;
        }

        set => _dispstat = (ushort)(value & 0xFF38);
    }

    internal static void Start(bool paced)
    {
        _pacer = paced ? new FramePacer() : null;
        _line = 0;
        _inHBlank = false;
        HBlankEvent.Schedule(CyclesUntilHBlank);
    }

    internal static void BeforeVideoMemoryWrite(int kilobyte) => Frame.CopyBeforeWrite(kilobyte);

    internal static void ReloadReferencePoint(uint offset)
    {
        int index = Array.IndexOf(ReferencePointRegisters, offset & ~3u);
        uint value = IO.ReadRegister(offset & ~3u) | ((uint)IO.ReadRegister((offset & ~3u) + 2) << 16);

        ReferencePoints[index] = (int)(value << 4) >> 4;
    }

    private static void StartHBlank(long dueAt)
    {
        _inHBlank = true;

        if ((_dispstat & HBlankIRQEnable) != 0)
        {
            Interrupts.Raise(Interrupt.HBlank);
        }

        DMA.OnLine(_line);
        if (_line < VisibleLines)
        {
            Frame.CaptureLine(_line, ReferencePoints);
            for (int i = 0; i < ReferencePoints.Length; i++)
            {
                ReferencePoints[i] += (short)IO.ReadRegister(ReferencePointSteps[i]);
            }

            DMA.OnHBlank();
        }

        LineEvent.ScheduleAt(dueAt + CyclesPerLine - CyclesUntilHBlank);
    }

    private static void StartLine(long dueAt)
    {
        _inHBlank = false;
        _line = (_line + 1) % TotalLines;
        Keypad.CheckInterrupt();

        if (_line == _dispstat >> 8 && (_dispstat & VCountIRQEnable) != 0)
        {
            Interrupts.Raise(Interrupt.VCount);
        }

        if (_line == VisibleLines)
        {
            if ((_dispstat & VBlankIRQEnable) != 0)
            {
                Interrupts.Raise(Interrupt.VBlank);
            }

            Frame.CaptureMemory();
            FrameFinished?.Invoke(Frame);
            _pacer?.WaitForNextFrame();
            DMA.OnVBlank();
        }
        else if (_line == 0)
        {
            foreach (uint register in ReferencePointRegisters)
            {
                ReloadReferencePoint(register);
            }
        }

        HBlankEvent.ScheduleAt(dueAt + CyclesUntilHBlank);
    }
}
