namespace AGBModern;

internal static class Serial
{
    public const uint DataStart = 0x120;
    public const uint DataEnd = 0x12C;
    public const uint RCNT = 0x134;
    public const uint JOYCNT = 0x140;
    public const uint JOY_RECV = 0x150;
    public const uint JOYEnd = 0x15A;

    private const uint SIOCNT = 0x128;
    private const uint SIODATA8 = 0x12A;
    private const uint JOY_TRANS = 0x154;
    private const uint JOYSTAT = 0x158;

    private const ushort InternalClock = 1 << 0;
    private const ushort FastClock = 1 << 1;
    private const ushort SIHigh = 1 << 2;
    private const ushort SDHigh = 1 << 3;
    private const ushort SOWhenIdle = 1 << 3;
    private const ushort Start = 1 << 7;
    private const ushort IRQEnable = 1 << 14;

    private const ushort CTS = 1 << 2;
    private const ushort SendFull = 1 << 4;
    private const ushort ReceiveEmpty = 1 << 5;
    private const ushort EightBits = 1 << 7;
    private const ushort FIFOEnable = 1 << 8;
    private const ushort ParityEnable = 1 << 9;
    private const ushort SendEnable = 1 << 10;
    private const ushort ReceiveEnable = 1 << 11;

    private const ushort NormalWritable = 0x7F8B;
    private const ushort MultiplayerWritable = 0x7F03;
    private const ushort UARTWritable = 0x7F8F;
    private const ushort RCNTAtPowerOn = 0x8000;
    private const ushort RCNTWritable = 0xC1FF;
    private const ushort JOYCNTWritable = 0x0040;
    private const ushort JOYSTATWritable = 0x0030;
    private const ushort JOYSendStatus = 1 << 1;
    private const ushort JOYReceiveStatus = 1 << 3;

    private const int SC = 0;
    private const int SD = 1;
    private const int SI = 2;
    private const int SO = 3;
    private const int UARTFIFOSize = 4;

    private enum Mode
    {
        Normal8,
        Normal32,
        Multiplayer,
        UART,
        GeneralPurpose,
        JOYBus,
    }

    private static readonly int[] BaudRates = [9600, 38400, 57600, 115200];

    private static readonly ushort[] Data = new ushort[(DataEnd - DataStart) / 2];
    private static readonly ushort[] JOYData = new ushort[(JOYSTAT - JOY_RECV) / 2];
    private static readonly Scheduler.Event TransferEvent = Scheduler.CreateEvent(FinishTransfer);

    private static ushort _control;
    private static ushort _rcnt = RCNTAtPowerOn;
    private static ushort _joyControl;
    private static ushort _joyStatus;
    private static long _transferStartedAt;
    private static int _cyclesPerBit;
    private static int _uartQueued;

    private static Mode CurrentMode => (_rcnt >> 14) switch
    {
        2 => Mode.GeneralPurpose,
        3 => Mode.JOYBus,
        _ => (Mode)((_control >> 12) & 3),
    };

    private static bool IsNormal => CurrentMode is Mode.Normal8 or Mode.Normal32;

    private static int TransferBits => CurrentMode == Mode.Normal32 ? 32 : 8;

    private static bool IsShifting => IsNormal && TransferEvent.When != long.MaxValue;

    private static bool IsUARTFull => _uartQueued >= ((_control & FIFOEnable) != 0 ? UARTFIFOSize + 1 : 1);

    public static ushort Read16(uint offset)
    {
        return offset switch
        {
            SIOCNT => ReadControl(),
            RCNT => (ushort)((_rcnt & ~0xF) | Pins()),
            JOYCNT => _joyControl,
            JOYSTAT => _joyStatus,
            >= JOY_RECV and < JOYSTAT => ReadJOYData(offset),
            _ when IsShifting => ShiftingData(offset),
            _ => Data[(offset - DataStart) / 2],
        };
    }

    public static void Write16(uint offset, ushort value)
    {
        switch (offset)
        {
            case SIOCNT:
                WriteControl(value);
                break;
            case RCNT:
                var before = CurrentMode;
                _rcnt = (ushort)(value & RCNTWritable);
                if (CurrentMode != before)
                {
                    Stop();
                }

                break;
            case JOYCNT:
                _joyControl = (ushort)((_joyControl & ~(value & 7) & 7) | (value & JOYCNTWritable));
                break;
            case JOYSTAT:
                _joyStatus = (ushort)((_joyStatus & ~JOYSTATWritable) | (value & JOYSTATWritable));
                break;
            case >= JOY_RECV and < JOYSTAT:
                JOYData[(offset - JOY_RECV) / 2] = value;
                if (offset >= JOY_TRANS)
                {
                    _joyStatus |= JOYSendStatus;
                }

                break;
            case SIODATA8 when CurrentMode == Mode.UART:
                Data[(offset - DataStart) / 2] = value;
                QueueUARTByte();
                break;
            default:
                Data[(offset - DataStart) / 2] = value;
                break;
        }
    }

    private static ushort ReadControl()
    {
        return CurrentMode switch
        {
            Mode.Normal8 or Mode.Normal32 => (ushort)(_control | SIHigh),
            Mode.Multiplayer => (ushort)(_control | SIHigh | SDHigh),
            Mode.UART => (ushort)(_control | ReceiveEmpty | (IsUARTFull ? SendFull : 0)),
            _ => _control,
        };
    }

    private static void WriteControl(ushort value)
    {
        var before = CurrentMode;
        bool wasTransferring = (_control & Start) != 0;

        ushort writable = (Mode)((value >> 12) & 3) switch
        {
            Mode.Multiplayer => MultiplayerWritable,
            Mode.UART => UARTWritable,
            _ => NormalWritable,
        };

        _control = (ushort)((_control & ~writable) | (value & writable));
        if (CurrentMode != before)
        {
            Stop();
            return;
        }

        switch (CurrentMode)
        {
            case Mode.Normal8 or Mode.Normal32 when (_control & Start) == 0:
                TransferEvent.Cancel();
                break;
            case Mode.Normal8 or Mode.Normal32 when !wasTransferring && (_control & InternalClock) != 0:
                _cyclesPerBit = (_control & FastClock) != 0 ? Scheduler.CyclesPerSecond / 2097152 : Scheduler.CyclesPerSecond / 262144;
                _transferStartedAt = Scheduler.Cycles;
                TransferEvent.ScheduleAt(_transferStartedAt + (TransferBits * _cyclesPerBit));
                break;
            case Mode.UART when (_control & FIFOEnable) == 0:
                _uartQueued = Math.Min(_uartQueued, 1);
                ContinueUART();
                break;
            case Mode.UART:
                ContinueUART();
                break;
        }
    }

    private static void Stop()
    {
        TransferEvent.Cancel();
        _control &= unchecked((ushort)~Start);
        _uartQueued = 0;
    }

    private static uint Shifting()
    {
        int bits = TransferBits;
        int shifted = IsShifting ? (int)Math.Min((Scheduler.Cycles - _transferStartedAt) / _cyclesPerBit, bits) : 0;
        if (shifted == 32)
        {
            return uint.MaxValue;
        }

        uint sent = bits == 32 ? Data[0] | ((uint)Data[1] << 16) : (uint)(Data[5] & 0xFF);
        uint value = (sent << shifted) | ((1u << shifted) - 1);
        return bits == 32 ? value : value & 0xFF;
    }

    private static ushort ShiftingData(uint offset)
    {
        uint value = Shifting();
        return offset switch
        {
            DataStart when TransferBits == 32 => (ushort)value,
            DataStart + 2 when TransferBits == 32 => (ushort)(value >> 16),
            SIODATA8 when TransferBits == 8 => (ushort)((Data[5] & 0xFF00) | (int)(value & 0xFF)),
            _ => Data[(offset - DataStart) / 2],
        };
    }

    private static void FinishTransfer(long dueAt)
    {
        if (CurrentMode == Mode.UART)
        {
            _uartQueued--;
            ContinueUART();
            return;
        }

        if (TransferBits == 32)
        {
            (Data[0], Data[1]) = (0xFFFF, 0xFFFF);
        }
        else
        {
            Data[5] |= 0xFF;
        }

        _control &= unchecked((ushort)~Start);
        if ((_control & IRQEnable) != 0)
        {
            Interrupts.Raise(Interrupt.Serial);
        }
    }

    private static void QueueUARTByte()
    {
        if (IsUARTFull)
        {
            return;
        }

        _uartQueued++;
        if (IsUARTFull && (_control & IRQEnable) != 0)
        {
            Interrupts.Raise(Interrupt.Serial);
        }

        ContinueUART();
    }

    private static void ContinueUART()
    {
        bool canSend = (_control & SendEnable) != 0 && (_control & CTS) == 0;
        if (_uartQueued == 0 || !canSend || TransferEvent.When != long.MaxValue)
        {
            return;
        }

        int bits = 1 + ((_control & EightBits) != 0 ? 8 : 7) + ((_control & ParityEnable) != 0 ? 1 : 0) + 1;
        TransferEvent.ScheduleAt(Scheduler.Cycles + ((long)bits * Scheduler.CyclesPerSecond / BaudRates[_control & 3]));
    }

    private static int Pins()
    {
        int high = (1 << SC) | (1 << SD) | (1 << SI) | (1 << SO);
        switch (CurrentMode)
        {
            case Mode.GeneralPurpose:
                int outputs = (_rcnt >> 4) & 0xF;
                return (_rcnt & outputs) | (high & ~outputs);
            case Mode.JOYBus:
                return high & ~((1 << SC) | (1 << SD));
            case Mode.Normal8 or Mode.Normal32 when (_control & Start) == 0:
                return (high & ~(1 << SO)) | ((_control & SOWhenIdle) != 0 ? 1 << SO : 0);
            case Mode.Normal8 or Mode.Normal32:
                int bit = (int)(Shifting() >> (TransferBits - 1)) & 1;
                return (high & ~(1 << SO)) | (bit << SO);
            case Mode.UART when (_control & ReceiveEnable) != 0:
                return high & ~(1 << SD);
            default:
                return high;
        }
    }

    private static ushort ReadJOYData(uint offset)
    {
        if (offset < JOY_TRANS)
        {
            _joyStatus &= unchecked((ushort)~JOYReceiveStatus);
        }

        return JOYData[(offset - JOY_RECV) / 2];
    }
}
