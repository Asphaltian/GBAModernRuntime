using AGBModern.PSG;

namespace AGBModern;

/// <summary>The GBA's sound. Subscribe to <see cref="SamplesReady"/> and play whatever it gives you.</summary>
public static class APU
{
    private const int CyclesPerSequencerStep = Scheduler.CyclesPerSecond / 512;

    private const uint FIFOA = 0x040000A0;
    private const uint FIFOB = 0x040000A4;

    private static readonly SquareChannel Square1 = new();
    private static readonly SquareChannel Square2 = new();
    private static readonly WaveChannel Wave = new();
    private static readonly NoiseChannel Noise = new();
    private static readonly DirectSound[] DirectSounds = [new(FIFOA), new(FIFOB)];

    private static readonly short[] Buffer = new short[2 * 512];
    private static readonly Scheduler.Event CatchUpEvent = Scheduler.CreateEvent(CatchUp);

    private static readonly byte[] Registers = new byte[0x30];
    private static readonly byte[] ReadableBits =
    [
        0x7F, 0x00, 0xC0, 0xFF, 0x00, 0x40, 0x00, 0x00,
        0xC0, 0xFF, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00,
        0xE0, 0x00, 0x00, 0xE0, 0x00, 0x40, 0x00, 0x00,
        0x00, 0xFF, 0x00, 0x00, 0xFF, 0x40, 0x00, 0x00,
        0x77, 0xFF, 0x0F, 0x77, 0x80, 0x00, 0x00, 0x00,
        0xFE, 0xC3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    private static bool _isOn;

    private static long _generatedUntil;
    private static int _buffered;
    private static int _sequencerCycles;
    private static int _sequencerStep;

    private static int PSGControl => Registers[0x20] | (Registers[0x21] << 8);

    private static int DirectSoundControl => Registers[0x22] | (Registers[0x23] << 8);

    private static int Resolution => Registers[0x29] >> 6;

    private static int CyclesPerSample => 512 >> Resolution;

    /// <summary>How many samples a second the game is making right now. Keep in mind that the game can change this whenever it wants.</summary>
    public static int SampleRate => 32768 << Resolution;

    /// <summary>
    /// Gives you interleaved stereo samples along with their sample rate. This runs on the game's
    /// thread, and the samples are only yours until you return, so copy them if you need them later.
    /// </summary>
    public static event Action<ReadOnlySpan<short>, int>? SamplesReady;

    internal static void Start()
    {
        Registers[0x29] = 0x02;
        Timers.Overflowed += OnTimerOverflow;
        _generatedUntil = Scheduler.Cycles;
        CatchUp(Scheduler.Cycles);
    }

    internal static byte Read8(uint offset)
    {
        return offset switch
        {
            0x84 => (byte)((_isOn ? 0x80 : 0)
                | (Square1.IsEnabled ? 1 : 0) | (Square2.IsEnabled ? 2 : 0)
                | (Wave.IsEnabled ? 4 : 0) | (Noise.IsEnabled ? 8 : 0)),
            >= 0x90 and < 0xA0 => Wave.ReadWaveRAM((int)offset - 0x90),
            < 0x90 => (byte)(Registers[offset - 0x60] & ReadableBits[offset - 0x60]),
            _ => 0,
        };
    }

    internal static void Write8(uint offset, byte value)
    {
        if (offset is >= 0xA0 and < 0xA8)
        {
            DirectSounds[(offset - 0xA0) / 4].Push(value);
            return;
        }

        Scheduler.RunDueEvents();
        GenerateSamples(Scheduler.Cycles);

        if (offset == 0x89 && value >> 6 != Resolution)
        {
            FlushBuffer();
        }

        if (!_isOn && offset < 0x82)
        {
            return;
        }

        if (offset < 0x90)
        {
            Registers[offset - 0x60] = value;
        }

        switch (offset)
        {
            case 0x60:
                Square1.WriteSweep(value);
                break;
            case 0x62:
                Square1.WriteDutyLength(value);
                break;
            case 0x63:
                Square1.WriteEnvelope(value);
                break;
            case 0x64:
                Square1.WriteFrequencyLow(value);
                break;
            case 0x65:
                Square1.WriteFrequencyHigh(value);
                break;
            case 0x68:
                Square2.WriteDutyLength(value);
                break;
            case 0x69:
                Square2.WriteEnvelope(value);
                break;
            case 0x6C:
                Square2.WriteFrequencyLow(value);
                break;
            case 0x6D:
                Square2.WriteFrequencyHigh(value);
                break;
            case 0x70:
                Wave.WriteControl(value);
                break;
            case 0x72:
                Wave.WriteLength(value);
                break;
            case 0x73:
                Wave.WriteVolume(value);
                break;
            case 0x74:
                Wave.WriteFrequencyLow(value);
                break;
            case 0x75:
                Wave.WriteFrequencyHigh(value);
                break;
            case 0x78:
                Noise.WriteLength(value);
                break;
            case 0x79:
                Noise.WriteEnvelope(value);
                break;
            case 0x7C:
                Noise.WriteFrequency(value);
                break;
            case 0x7D:
                Noise.WriteControl(value);
                break;
            case 0x83:
                if ((value & 0x08) != 0)
                {
                    DirectSounds[0].Clear();
                }

                if ((value & 0x80) != 0)
                {
                    DirectSounds[1].Clear();
                }

                break;
            case 0x84:
                SetMasterSwitch((value & 0x80) != 0);
                break;
            case >= 0x90 and < 0xA0:
                Wave.WriteWaveRAM((int)offset - 0x90, value);
                break;
        }
    }

    internal static void ClearWaveRAM() => Wave.ClearWaveRAM();

    private static void SetMasterSwitch(bool isOn)
    {
        if (_isOn && !isOn)
        {
            Square1.Reset();
            Square2.Reset();
            Wave.Reset();
            Noise.Reset();
            Registers.AsSpan(0, 0x22).Clear();
        }

        _isOn = isOn;
    }

    private static void OnTimerOverflow(int timer, long overflowedAt)
    {
        if (timer > 1)
        {
            return;
        }

        GenerateSamples(overflowedAt);
        for (int i = 0; i < DirectSounds.Length; i++)
        {
            int selectedTimer = (DirectSoundControl >> (10 + (i * 4))) & 1;
            if (selectedTimer == timer)
            {
                DirectSounds[i].NextSample();
            }
        }
    }

    private static void CatchUp(long dueAt)
    {
        GenerateSamples(dueAt);
        CatchUpEvent.ScheduleAt(dueAt + CyclesPerSequencerStep);
    }

    private static void GenerateSamples(long until)
    {
        int cyclesPerSample = CyclesPerSample;
        for (; _generatedUntil + cyclesPerSample <= until; _generatedUntil += cyclesPerSample)
        {
            Square1.Advance(cyclesPerSample);
            Square2.Advance(cyclesPerSample);
            Wave.Advance(cyclesPerSample);
            Noise.Advance(cyclesPerSample);

            _sequencerCycles += cyclesPerSample;
            if (_sequencerCycles >= CyclesPerSequencerStep)
            {
                _sequencerCycles -= CyclesPerSequencerStep;
                ClockSequencer();
            }

            Buffer[_buffered++] = Mix(isLeft: true);
            Buffer[_buffered++] = Mix(isLeft: false);
            if (_buffered == Buffer.Length)
            {
                FlushBuffer();
            }
        }
    }

    private static void FlushBuffer()
    {
        SamplesReady?.Invoke(Buffer.AsSpan(0, _buffered), SampleRate);
        _buffered = 0;
    }

    private static void ClockSequencer()
    {
        _sequencerStep = (_sequencerStep + 1) & 7;

        if ((_sequencerStep & 1) == 0)
        {
            Square1.ClockLength();
            Square2.ClockLength();
            Wave.ClockLength();
            Noise.ClockLength();
        }

        if ((_sequencerStep & 3) == 2)
        {
            Square1.ClockSweep();
        }

        if (_sequencerStep == 7)
        {
            Square1.ClockEnvelope();
            Square2.ClockEnvelope();
            Noise.ClockEnvelope();
        }
    }

    private static short Mix(bool isLeft)
    {
        if (!_isOn)
        {
            return 0;
        }

        int enables = PSGControl >> (isLeft ? 12 : 8);
        int psg = ((enables & 1) != 0 ? Square1.Output : 0)
            + ((enables & 2) != 0 ? Square2.Output : 0)
            + ((enables & 4) != 0 ? Wave.Output : 0)
            + ((enables & 8) != 0 ? Noise.Output : 0);

        int masterVolume = (PSGControl >> (isLeft ? 4 : 0)) & 7;
        int psgShift = (DirectSoundControl & 3) switch
        {
            0 => 2,
            1 => 1,
            _ => 0,
        };
        int sample = (psg * (masterVolume + 1)) >> psgShift;

        for (int i = 0; i < DirectSounds.Length; i++)
        {
            bool isEnabled = (DirectSoundControl & (1 << (8 + (i * 4) + (isLeft ? 1 : 0)))) != 0;
            bool isFullVolume = (DirectSoundControl & (4 << i)) != 0;
            if (isEnabled)
            {
                sample += DirectSounds[i].Sample * (isFullVolume ? 4 : 2);
            }
        }

        int level = (Registers[0x28] | (Registers[0x29] << 8)) & 0x3FE;
        int droppedBits = Resolution + 1;
        int output = (Math.Clamp(sample + level, 0, 0x3FF) >> droppedBits) << droppedBits;
        return (short)Math.Clamp((output - level) << 6, short.MinValue, short.MaxValue);
    }

    private sealed class DirectSound(uint fifoAddress)
    {
        private const int DMARequestLevel = 16;

        private readonly byte[] _fifo = new byte[32];
        private int _readPosition;
        private int _count;

        public int Sample { get; private set; }

        public void Push(byte value)
        {
            if (_count < _fifo.Length)
            {
                _fifo[(_readPosition + _count++) % _fifo.Length] = value;
            }
        }

        public void Clear()
        {
            _count = 0;
            Sample = 0;
        }

        public void NextSample()
        {
            if (_count > 0)
            {
                Sample = (sbyte)_fifo[_readPosition];
                _readPosition = (_readPosition + 1) % _fifo.Length;
                _count--;
            }

            if (_count <= DMARequestLevel)
            {
                DMA.OnSoundFIFO(fifoAddress);
            }
        }
    }
}
