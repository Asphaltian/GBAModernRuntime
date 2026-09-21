namespace AGBModern;

/// <summary>The I/O registers at 0x04000000. All the constants here are offsets from there.</summary>
public static class IO
{
    public const uint BG0CNT = 0x008;
    public const uint KEYCNT = 0x132;
    public const uint IE = 0x200;
    public const uint IF = 0x202;
    public const uint IME = 0x208;

    internal const uint DISPCNT = 0x000;

    private const uint DISPSTAT = 0x004;
    private const uint VCOUNT = 0x006;
    private const uint BG1CNT = 0x00A;
    private const uint BG2X = 0x028;
    private const uint BG3X = 0x038;
    private const uint SOUND1CNT_L = 0x060;
    private const uint DMA0SAD = 0x0B0;
    private const uint TM0CNT_L = 0x100;
    private const uint KEYINPUT = 0x130;
    private const uint WAITCNT = 0x204;
    private const uint POSTFLG = 0x300;
    private const uint WININ = 0x048;
    private const uint WINOUT = 0x04A;
    private const uint BLDCNT = 0x050;
    private const uint BLDALPHA = 0x052;
    private const uint InternalMemoryControl = 0x800;
    private const uint DMAEnd = DMA0SAD + (4 * 12);
    private const uint TimersEnd = TM0CNT_L + (4 * 4);
    private const ushort CGBMode = 1 << 3;
    private const uint MemoryControlReadable = 0xFF00002F;
    internal const uint MemoryControlAtPowerOn = 0x0D000020;

    private static readonly ushort[] Written = new ushort[0x200];
    private static readonly bool[] IsHidden = CreateHiddenMap();

    private static uint _memoryControl = MemoryControlAtPowerOn;

    /// <summary>Reads a register the way the game would see it, so write-only registers read as 0. This doesn't cost any cycles.</summary>
    public static ushort Read16(uint address)
    {
        uint offset = address & 0x00FFFFFE;
        return offset < 0x400 && IsHidden[offset >> 1] ? (ushort)0 : ReadRegister(address);
    }

    internal static ushort Read16(uint address, uint openBus)
    {
        uint offset = address & 0x00FFFFFE;
        return IsUnreadable(offset) && IsUnreadable(offset ^ 2) ? (ushort)(openBus >> (int)((offset & 2) * 8)) : Read16(address);
    }

    private static bool IsUnreadable(uint offset) => offset < 0x400
        ? IsHidden[offset >> 1]
        : (offset & 0xFFFC) != InternalMemoryControl && offset is not (>= DebugPorts.Start and < DebugPorts.End);

    internal static ushort ReadRegister(uint address)
    {
        uint offset = address & 0x00FFFFFE;
        return offset switch
        {
            _ when (offset & 0xFFFC) == InternalMemoryControl => (ushort)(_memoryControl >> (int)((offset & 2) * 8)),
            >= DebugPorts.Start and < DebugPorts.End => DebugPorts.Read16(offset),
            >= 0x400 => 0,
            DISPSTAT => Video.DISPSTAT,
            VCOUNT => Video.VCOUNT,
            >= SOUND1CNT_L and < DMA0SAD => (ushort)(APU.Read8(offset) | (APU.Read8(offset + 1) << 8)),
            >= DMA0SAD and < DMAEnd => DMA.Read16((int)(offset - DMA0SAD) / 12, (int)(offset - DMA0SAD) % 12),
            >= TM0CNT_L and < TimersEnd => (offset & 2) == 0
                ? Timers.ReadCounter((int)(offset - TM0CNT_L) / 4)
                : Timers.ReadControl((int)(offset - TM0CNT_L) / 4),
            >= Serial.DataStart and < Serial.DataEnd or Serial.RCNT or Serial.JOYCNT or >= Serial.JOY_RECV and < Serial.JOYEnd => Serial.Read16(offset),
            KEYINPUT => Keypad.KEYINPUT,
            _ => Written[offset >> 1],
        };
    }

    /// <summary>Writes a register just like the game would, but without costing any cycles.</summary>
    public static void Write16(uint address, ushort value)
    {
        uint offset = address & 0x00FFFFFE;
        if ((offset & 0xFFFC) == InternalMemoryControl)
        {
            int shift = (int)((offset & 2) * 8);
            _memoryControl = (uint)((_memoryControl & ~(0xFFFFu << shift)) | ((uint)value << shift)) & MemoryControlReadable;
            Memory.SetWaitstates(Written[WAITCNT >> 1], _memoryControl);
            return;
        }

        if (offset >= 0x400)
        {
            DebugPorts.Write(offset, (byte)value);
            return;
        }

        if (offset == DISPCNT)
        {
            value = (ushort)((value & ~CGBMode) | (Written[0] & CGBMode));
        }

        if (offset == IF)
        {
            Written[offset >> 1] &= (ushort)~value;
            return;
        }

        Written[offset >> 1] = (ushort)(value & UsedBits(offset));

        switch (offset)
        {
            case DISPSTAT:
                Video.DISPSTAT = value;
                break;
            case >= BG2X and < BG2X + 8 or >= BG3X and < BG3X + 8:
                Video.ReloadReferencePoint(offset);
                break;
            case >= SOUND1CNT_L and < DMA0SAD:
                APU.Write8(offset, (byte)value);
                APU.Write8(offset + 1, (byte)(value >> 8));
                break;
            case >= DMA0SAD and < DMAEnd:
                DMA.Write16((int)(offset - DMA0SAD) / 12, (int)(offset - DMA0SAD) % 12, value);
                break;
            case >= TM0CNT_L and < TimersEnd when (offset & 2) == 0:
                Timers.WriteReload((int)(offset - TM0CNT_L) / 4, value);
                break;
            case >= TM0CNT_L and < TimersEnd:
                Timers.WriteControl((int)(offset - TM0CNT_L) / 4, value);
                break;
            case >= Serial.DataStart and < Serial.DataEnd or Serial.RCNT or Serial.JOYCNT or >= Serial.JOY_RECV and < Serial.JOYEnd:
                Serial.Write16(offset, value);
                break;
            case KEYCNT:
                Keypad.CheckInterrupt();
                break;
            case IE or IME:
                CheckInterrupts();
                break;
            case WAITCNT:
                Memory.SetWaitstates(value, _memoryControl);
                break;
            case POSTFLG:
                Power.WriteHALTCNT((byte)(value >> 8));
                break;
        }
    }

    /// <inheritdoc cref="Write16"/>
    public static void Write8(uint address, byte value)
    {
        uint offset = address & 0x00FFFFFE;
        int shift = (int)(address & 1) * 8;

        switch (offset)
        {
            case >= 0x400:
                DebugPorts.Write(address & 0x00FFFFFF, value);
                break;

            case >= SOUND1CNT_L and < DMA0SAD:
                APU.Write8(address & 0x00FFFFFF, value);
                break;

            case IF:
                Write16(offset, (ushort)(value << shift));
                break;

            case POSTFLG when shift == 8:
                Power.WriteHALTCNT(value);
                break;

            case POSTFLG:
                Written[offset >> 1] = (ushort)(value & UsedBits(POSTFLG));
                break;

            default:
                Write16(offset, (ushort)((Written[offset >> 1] & ~(0xFF << shift)) | (value << shift)));
                break;
        }
    }

    private static ushort UsedBits(uint offset) => offset switch
    {
        BG0CNT or BG1CNT => 0xDFFF,
        WININ or WINOUT => 0x3F3F,
        BLDCNT => 0x3FFF,
        BLDALPHA => 0x1F1F,
        KEYCNT => 0xC3FF,
        WAITCNT => 0x5FFF,
        IME or POSTFLG => 0x0001,
        _ => 0xFFFF,
    };

    private static bool[] CreateHiddenMap()
    {
        bool[] hidden = new bool[0x200];
        foreach (var (start, end) in ((uint, uint)[])[
            (0x010, 0x048), (0x04C, 0x050), (0x054, 0x060), (0x066, 0x068), (0x06A, 0x06C), (0x06E, 0x070),
            (0x076, 0x078), (0x07A, 0x07C), (0x07E, 0x080), (0x086, 0x088), (0x08A, 0x090), (0x0A0, 0x0B0),
            (0x0E0, 0x100), (0x110, 0x120), (0x12C, 0x130), (0x136, 0x140), (0x142, 0x150), (0x15A, 0x200),
            (0x206, 0x208), (0x20A, 0x300), (0x302, 0x400)])
        {
            hidden.AsSpan((int)(start >> 1), (int)((end - start) >> 1)).Fill(true);
        }

        for (uint channel = DMA0SAD; channel < DMAEnd; channel += 12)
        {
            hidden.AsSpan((int)(channel >> 1), 5).Fill(true);
        }

        return hidden;
    }

    internal static void SetInterruptFlags(ushort flags)
    {
        Written[IF >> 1] |= flags;
        CheckInterrupts();
    }

    private static void CheckInterrupts()
    {
        if (Interrupts.IsPending)
        {
            Scheduler.NextEvent = Scheduler.Cycles;
        }
    }
}
