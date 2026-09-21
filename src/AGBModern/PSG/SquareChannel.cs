namespace AGBModern.PSG;

internal sealed class SquareChannel
{
    private static readonly byte[] DutyPatterns = [0b00000001, 0b10000001, 0b10000111, 0b01111110];

    private Envelope _envelope;
    private int _duty;
    private int _frequency;
    private int _timer;
    private int _step;
    private LengthTimer _length = new(64);

    private int _sweepShift;
    private bool _sweepDecreases;
    private int _sweepTime;
    private int _sweepTimer;
    private int _sweepFrequency;

    public bool IsEnabled { get; private set; }

    public int Output
    {
        get
        {
            if (!IsEnabled)
            {
                return 0;
            }

            bool isHigh = (DutyPatterns[_duty] & (1 << _step)) != 0;
            return isHigh ? _envelope.Volume : -_envelope.Volume;
        }
    }

    private int CyclesPerStep => (2048 - _frequency) * 16;

    public void WriteSweep(byte value)
    {
        _sweepShift = value & 7;
        _sweepDecreases = (value & 8) != 0;
        _sweepTime = (value >> 4) & 7;
    }

    public void WriteDutyLength(byte value)
    {
        _length.Write(value & 63);
        _duty = value >> 6;
    }

    public void WriteEnvelope(byte value)
    {
        _envelope.Write(value);
        IsEnabled &= _envelope.IsDACOn;
    }

    public void WriteFrequencyLow(byte value)
    {
        _frequency = (_frequency & 0x700) | value;
    }

    public void WriteFrequencyHigh(byte value)
    {
        _frequency = (_frequency & 0xFF) | ((value & 7) << 8);
        _length.WriteControl(value);

        if ((value & 0x80) != 0)
        {
            Restart();
        }
    }

    public void Reset()
    {
        WriteSweep(0);
        WriteDutyLength(0);
        WriteEnvelope(0);
        WriteFrequencyLow(0);
        WriteFrequencyHigh(0);
    }

    public void Advance(int cycles)
    {
        if (!IsEnabled)
        {
            return;
        }

        for (_timer -= cycles; _timer <= 0; _timer += CyclesPerStep)
        {
            _step = (_step + 1) & 7;
        }
    }

    public void ClockLength()
    {
        if (_length.Clock())
        {
            IsEnabled = false;
        }
    }

    public void ClockEnvelope() => _envelope.Clock();

    public void ClockSweep()
    {
        if (--_sweepTimer > 0)
        {
            return;
        }

        _sweepTimer = _sweepTime == 0 ? 8 : _sweepTime;
        if (_sweepTime == 0)
        {
            return;
        }

        int next = NextSweepFrequency();
        if (next <= 2047 && _sweepShift != 0)
        {
            _sweepFrequency = next;
            _frequency = next;
            NextSweepFrequency();
        }
    }

    private void Restart()
    {
        IsEnabled = _envelope.IsDACOn;
        _length.Restart();

        _timer = CyclesPerStep;
        _envelope.Restart();

        _sweepFrequency = _frequency;
        _sweepTimer = _sweepTime == 0 ? 8 : _sweepTime;
        if (_sweepShift != 0)
        {
            NextSweepFrequency();
        }
    }

    private int NextSweepFrequency()
    {
        int change = _sweepFrequency >> _sweepShift;
        int next = _sweepDecreases ? _sweepFrequency - change : _sweepFrequency + change;
        if (next > 2047)
        {
            IsEnabled = false;
        }

        return next;
    }
}
