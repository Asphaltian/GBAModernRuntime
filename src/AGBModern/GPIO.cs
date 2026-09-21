using System.Buffers.Binary;

namespace AGBModern;

/// <summary>What a cartridge has on its GPIO port.</summary>
[Flags]
public enum GPIODevices
{
    None = 0,
    RTC = 1 << 0,
    Rumble = 1 << 1,
}

internal readonly record struct ClockState(TimeSpan Offset, int DayOfWeekOffset, byte Control)
{
    private const byte ControlAfterBatteryShortcut = 0x82;

    public static ClockState Default => new(TimeSpan.Zero, 0, ControlAfterBatteryShortcut);
}

/// <summary>The cartridge's GPIO port, along with the real-time clock and the rumble motor some cartridges have on it.</summary>
public static class GPIO
{
    private const uint DataRegister = 0xC4;
    private const uint DirectionRegister = 0xC6;
    private const uint ControlRegister = 0xC8;
    private const int RegistersEnd = 0xCA;

    private const int ClockPin = 1 << 0;
    private const int SerialPin = 1 << 1;
    private const int SelectPin = 1 << 2;
    private const int RumblePin = 1 << 3;

    private static readonly byte[] CoveredROM = new byte[RegistersEnd - DataRegister];

    private static int _pins;
    private static int _direction;
    private static bool _isReadable;
    private static bool _isRumbling;

    /// <summary>What the cartridge has on its port.</summary>
    public static GPIODevices Devices { get; private set; }

    /// <summary>
    /// Where the real-time clock gets its time from. It's the system clock unless you swap it out, for
    /// example to try a game at another time of day.
    /// </summary>
    public static TimeProvider Host { get; set; } = TimeProvider.System;

    internal static ClockState Clock
    {
        get => RTC.State;
        set => RTC.State = value;
    }

    /// <summary>Tells you when the rumble motor starts and stops. This runs on the game's thread.</summary>
    public static event Action<bool>? RumbleChanged;

    internal static void Connect(GPIODevices devices)
    {
        Devices = devices;
        _pins = 0;
        _direction = 0;
        _isReadable = false;
        _isRumbling = false;
        RTC.Reset();
        Memory.ROM.AsSpan((int)DataRegister, CoveredROM.Length).CopyTo(CoveredROM);
    }

    internal static bool Covers(uint address) => Devices != GPIODevices.None && (address & 0x1FFFFFE) is >= DataRegister and < RegistersEnd;

    internal static void Write16(uint address, ushort value)
    {
        int previous = _pins;
        switch (address & 0x1FFFFFE)
        {
            case DataRegister:
                _pins = (_pins & ~_direction) | (value & _direction & 0xF);
                break;
            case DirectionRegister:
                _direction = value & 0xF;
                if ((_direction & RumblePin) == 0)
                {
                    _pins &= ~RumblePin;
                }

                break;
            case ControlRegister:
                _isReadable = (value & 1) != 0;
                break;
        }

        if ((Devices & GPIODevices.RTC) != 0 && RTC.Signal(previous, _pins) is { } bit && (_direction & SerialPin) == 0)
        {
            _pins = (_pins & ~SerialPin) | (bit << 1);
        }

        bool isRumbling = (Devices & GPIODevices.Rumble) != 0 && (_pins & RumblePin) != 0;
        if (isRumbling != _isRumbling)
        {
            _isRumbling = isRumbling;
            RumbleChanged?.Invoke(isRumbling);
        }

        UpdateROM();
    }

    private static void UpdateROM()
    {
        var rom = Memory.ROM.AsSpan((int)DataRegister, CoveredROM.Length);
        if (!_isReadable)
        {
            CoveredROM.CopyTo(rom);
            return;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(rom, (ushort)_pins);
        BinaryPrimitives.WriteUInt16LittleEndian(rom[2..], (ushort)_direction);
        BinaryPrimitives.WriteUInt16LittleEndian(rom[4..], 1);
    }

    private static class RTC
    {
        private const int CommandCode = 0x6;
        private const int ReadBit = 0x80;
        private const byte Unused = 0xFF;
        private const byte WritableControl = 0x6A;
        private const byte Hour24 = 0x40;
        private const byte PM = 0x80;
        private const byte PowerOff = 0x80;

        private enum Command
        {
            ForceReset = 0,
            Alarm1 = 1,
            DateTime = 2,
            ForceIRQ = 3,
            Control = 4,
            Alarm2 = 5,
            Time = 6,
            Free = 7,
        }

        private static readonly int[] Lengths = [0, 1, 7, 0, 1, 3, 3, 1];
        private static readonly byte[] Parameters = new byte[7];

        private static bool _hasCommand;
        private static Command _command;
        private static bool _isReading;
        private static int _byte;
        private static int _bit;
        private static int _shift;

        public static ClockState State { get; set; } = ClockState.Default;

        private static DateTime HostNow => Host.GetLocalNow().DateTime;

        private static DateTime Now => HostNow + State.Offset;

        private static bool IsHour24 => (State.Control & Hour24) != 0;

        public static void Reset()
        {
            _hasCommand = false;
            _isReading = false;
            _byte = 0;
            _bit = 0;
            _shift = 0;
        }

        public static int? Signal(int previous, int pins)
        {
            bool wasSelected = (previous & SelectPin) != 0;
            bool isSelected = (pins & SelectPin) != 0;
            if (!isSelected || !wasSelected)
            {
                if (isSelected && (pins & ClockPin) != 0)
                {
                    Reset();
                }

                return null;
            }

            bool clockFell = (previous & ClockPin) != 0 && (pins & ClockPin) == 0;
            bool clockRose = (previous & ClockPin) == 0 && (pins & ClockPin) != 0;

            if (clockFell && _isReading && _byte < Lengths[(int)_command])
            {
                return (Parameters[_byte] >> _bit) & 1;
            }

            if (clockRose)
            {
                if (!_isReading)
                {
                    _shift |= ((pins & SerialPin) >> 1) << _bit;
                }

                if (++_bit == 8)
                {
                    FinishByte();
                }
            }

            return null;
        }

        private static void FinishByte()
        {
            _bit = 0;
            if (!_hasCommand)
            {
                StartCommand(_shift);
            }
            else if (!_isReading && _byte < Lengths[(int)_command])
            {
                Parameters[_byte++] = (byte)_shift;
                if (_byte == Lengths[(int)_command])
                {
                    Store();
                }
            }
            else
            {
                _byte++;
            }

            _shift = 0;
        }

        private static void StartCommand(int value)
        {
            if ((value & 0xF) != CommandCode)
            {
                return;
            }

            _hasCommand = true;
            _command = (Command)((value >> 4) & 7);
            _isReading = (value & ReadBit) != 0;
            _byte = 0;

            switch (_command)
            {
                case Command.ForceReset:
                    State = new ClockState(new DateTime(2000, 1, 1) - HostNow, -(int)new DateTime(2000, 1, 1).DayOfWeek, 0);
                    break;
                case Command.DateTime:
                    Load(Now, Parameters);
                    break;
                case Command.Time:
                    Load(Now, Parameters);
                    Parameters.AsSpan(4, 3).CopyTo(Parameters);
                    break;
                case Command.ForceIRQ:
                    Interrupts.Raise(Interrupt.GamePak);
                    break;
                case Command.Control:
                    Parameters[0] = State.Control;
                    if (_isReading)
                    {
                        State = State with { Control = (byte)(State.Control & ~PowerOff) };
                    }

                    break;
                case Command.Alarm1 or Command.Alarm2 or Command.Free:
                    Parameters.AsSpan().Fill(Unused);
                    break;
            }
        }

        private static void Store()
        {
            switch (_command)
            {
                case Command.Control:
                    State = State with { Control = (byte)(Parameters[0] & WritableControl) };
                    break;
                case Command.DateTime when ToDateTime(Parameters) is { } time:
                    Set(time, Parameters[3] & 7);
                    break;
                case Command.Time when ToTime(Parameters) is { } time:
                    Set(Now.Date + time, DayOfWeek(Now));
                    break;
            }
        }

        private static int DayOfWeek(DateTime time) => (((int)time.DayOfWeek + State.DayOfWeekOffset) % 7 + 7) % 7;

        private static void Set(DateTime time, int dayOfWeek)
        {
            State = State with { Offset = time - HostNow, DayOfWeekOffset = dayOfWeek - (int)time.DayOfWeek };
        }

        private static void Load(DateTime time, Span<byte> parameters)
        {
            parameters[0] = BCD(time.Year % 100);
            parameters[1] = BCD(time.Month);
            parameters[2] = BCD(time.Day);
            parameters[3] = (byte)DayOfWeek(time);
            parameters[4] = (byte)(BCD(IsHour24 ? time.Hour : time.Hour % 12) | (time.Hour >= 12 ? PM : 0));
            parameters[5] = BCD(time.Minute);
            parameters[6] = BCD(time.Second);
        }

        private static DateTime? ToDateTime(ReadOnlySpan<byte> parameters)
        {
            if (FromBCD(parameters[0]) is not { } year
                || FromBCD(parameters[1] & 0x1F) is not ({ } month and >= 1 and <= 12)
                || FromBCD(parameters[2] & 0x3F) is not { } day
                || day < 1 || day > DateTime.DaysInMonth(2000 + year, month)
                || ToTime(parameters[4..]) is not { } time)
            {
                return null;
            }

            return new DateTime(2000 + year, month, day) + time;
        }

        private static TimeSpan? ToTime(ReadOnlySpan<byte> parameters)
        {
            if (FromBCD(parameters[0] & 0x3F) is not { } hour
                || FromBCD(parameters[1] & 0x7F) is not ({ } minute and < 60)
                || FromBCD(parameters[2] & 0x7F) is not ({ } second and < 60))
            {
                return null;
            }

            if (!IsHour24)
            {
                hour = (hour % 12) + ((parameters[0] & PM) != 0 ? 12 : 0);
            }

            return hour < 24 ? new TimeSpan(hour, minute, second) : null;
        }

        private static byte BCD(int value) => (byte)(((value / 10) << 4) | (value % 10));

        private static int? FromBCD(int value) => (value >> 4) < 10 && (value & 0xF) < 10 ? ((value >> 4) * 10) + (value & 0xF) : null;
    }
}
