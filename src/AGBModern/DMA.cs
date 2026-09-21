namespace AGBModern;

internal static class DMA
{
    private const ushort Repeat = 1 << 9;
    private const ushort Is32Bit = 1 << 10;
    private const ushort IRQEnable = 1 << 14;
    private const ushort Enable = 1 << 15;
    private const int CaptureStart = 2;
    private const int CaptureEnd = 162;
    private const int GamePakStart = 0x8;
    private const int BIOSRegion = 0x0;
    private const uint GamePakBlockMask = 0x1FFFF;
    private const uint SoundFIFOUnits = 4;
    private const int NoChannel = 4;

    private enum Timing
    {
        Immediate,
        VBlank,
        HBlank,
        Special,
    }

    private static readonly Channel[] Channels = [new(0), new(1), new(2), new(3)];

    private static int _transferring = NoChannel;

    public static void Write16(int channel, int register, ushort value) => Channels[channel].Write16(register, value);

    public static ushort Read16(int channel, int register) => register == 10 ? Channels[channel].Control : (ushort)0;

    public static void OnVBlank() => Run(Timing.VBlank);

    public static void OnHBlank() => Run(Timing.HBlank);

    public static void OnLine(int line)
    {
        var channel = Channels[3];
        if (!channel.IsWaitingFor(Timing.Special))
        {
            return;
        }

        if (line == CaptureEnd)
        {
            channel.Stop();
        }
        else if (line is >= CaptureStart and < CaptureEnd)
        {
            Start(channel);
        }
    }

    public static void OnSoundFIFO(uint fifoAddress)
    {
        for (int i = 1; i <= 2; i++)
        {
            var channel = Channels[i];
            if (channel.IsWaitingFor(Timing.Special) && channel.Destination == fifoAddress)
            {
                Start(channel);
            }
        }
    }

    private static void Run(Timing timing)
    {
        foreach (var channel in Channels)
        {
            if (channel.IsWaitingFor(timing))
            {
                Start(channel);
            }
        }
    }

    private static void Start(Channel channel)
    {
        if (channel.Index >= _transferring)
        {
            channel.IsRequested = true;
            return;
        }

        int paused = _transferring;
        _transferring = channel.Index;
        channel.Transfer();
        _transferring = paused;

        foreach (var waiting in Channels)
        {
            if (waiting.IsRequested && waiting.Index < _transferring)
            {
                waiting.IsRequested = false;
                if (waiting.IsEnabled)
                {
                    Start(waiting);
                }
            }
        }
    }

    private sealed class Channel(int index)
    {
        private uint _sourceRegister;
        private uint _destinationRegister;
        private ushort _countRegister;

        private uint _source;
        private uint _count;
        private uint _latch;

        public int Index => index;

        public bool IsRequested { get; set; }

        public bool IsEnabled => (Control & Enable) != 0;

        public ushort Control { get; private set; }

        public uint Destination { get; private set; }

        public bool IsWaitingFor(Timing timing) => IsEnabled && (Timing)((Control >> 12) & 3) == timing;

        public void Write16(int register, ushort value)
        {
            switch (register)
            {
                case 0:
                    _sourceRegister = (_sourceRegister & 0xFFFF0000) | value;
                    break;
                case 2:
                    _sourceRegister = (_sourceRegister & 0x0000FFFF) | ((uint)value << 16);
                    break;
                case 4:
                    _destinationRegister = (_destinationRegister & 0xFFFF0000) | value;
                    break;
                case 6:
                    _destinationRegister = (_destinationRegister & 0x0000FFFF) | ((uint)value << 16);
                    break;
                case 8:
                    _countRegister = value;
                    break;
                case 10:
                    WriteControl(value);
                    break;
            }
        }

        public void Transfer()
        {
            bool isSoundFIFO = index is 1 or 2 && IsWaitingFor(Timing.Special);
            int unit = isSoundFIFO || (Control & Is32Bit) != 0 ? 4 : 2;
            int sourceStep = Step((Control >> 7) & 3, unit);
            int destinationStep = isSoundFIFO ? 0 : Step((Control >> 5) & 3, unit);
            uint count = isSoundFIFO ? SoundFIFOUnits : _count;

            bool isGamePakOnly = _source >> 24 >= GamePakStart && Destination >> 24 >= GamePakStart;
            Scheduler.Cycles += isGamePakOnly ? 4 : 2;

            for (bool isFirst = true; count > 0; count--, isFirst = false)
            {
                if (unit == 4)
                {
                    Memory.Write32(Destination, Read(Memory.Read32(_source, _latch)));
                }
                else
                {
                    Memory.Write16(Destination, (ushort)Read(Memory.Read16(_source, _latch) * 0x10001u));
                }

                if (!isFirst)
                {
                    Scheduler.Cycles -= SequentialSaving(_source) + SequentialSaving(Destination);
                }

                _source += (uint)sourceStep;
                Destination += (uint)destinationStep;

                if (Scheduler.Cycles >= Scheduler.NextEvent)
                {
                    Scheduler.RunDueEvents();
                }
            }

            Finish(reloadDestination: !isSoundFIFO && ((Control >> 5) & 3) == 3);
        }

        public void Stop()
        {
            Control &= unchecked((ushort)~Enable);
        }

        private void WriteControl(ushort value)
        {
            bool wasEnabled = IsEnabled;

            Control = (ushort)(value & (index == 3 ? 0xFFE0 : 0xF7E0));

            if (wasEnabled || !IsEnabled)
            {
                return;
            }

            _source = _sourceRegister & (index == 0 ? 0x07FFFFFFu : 0x0FFFFFFFu);
            ReloadDestination();
            ReloadCount();

            if (IsWaitingFor(Timing.Immediate))
            {
                Start(this);
            }
        }

        private void ReloadCount()
        {
            uint mask = index == 3 ? 0xFFFFu : 0x3FFFu;
            _count = (_countRegister & mask) == 0 ? mask + 1 : _countRegister & mask;
        }

        private static int SequentialSaving(uint address) => (address & GamePakBlockMask) == 0 ? 0 : Memory.SequentialSavingAt(address);

        private uint Read(uint value)
        {
            if (_source >> 24 != BIOSRegion)
            {
                _latch = value;
            }

            return _latch;
        }

        private void Finish(bool reloadDestination)
        {
            if ((Control & IRQEnable) != 0)
            {
                Interrupts.Raise((Interrupt)((ushort)Interrupt.DMA0 << index));
            }

            bool repeats = (Control & Repeat) != 0 && !IsWaitingFor(Timing.Immediate);
            if (!repeats)
            {
                Stop();
                return;
            }

            ReloadCount();
            if (reloadDestination)
            {
                ReloadDestination();
            }
        }

        private void ReloadDestination()
        {
            Destination = _destinationRegister & (index == 3 ? 0x0FFFFFFFu : 0x07FFFFFFu);
        }

        private static int Step(int addressControl, int unit) => addressControl switch
        {
            1 => -unit,
            2 => 0,
            _ => unit,
        };
    }
}
