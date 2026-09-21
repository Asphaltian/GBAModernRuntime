namespace AGBModern.PSG;

internal sealed class NoiseChannel
{
    private Envelope _envelope;
    private int _divider;
    private int _shift;
    private bool _is7Bit;
    private int _register;
    private bool _isHigh;
    private int _timer;
    private LengthTimer _length = new(64);

    public bool IsEnabled { get; private set; }

    public int Output => !IsEnabled ? 0 : _isHigh ? _envelope.Volume : -_envelope.Volume;

    private int CyclesPerShift => (_divider == 0 ? 16 : 32 * _divider) << (_shift + 1);

    public void WriteLength(byte value)
    {
        _length.Write(value & 63);
    }

    public void WriteEnvelope(byte value)
    {
        _envelope.Write(value);
        IsEnabled &= _envelope.IsDACOn;
    }

    public void WriteFrequency(byte value)
    {
        _divider = value & 7;
        _is7Bit = (value & 8) != 0;
        _shift = value >> 4;
    }

    public void WriteControl(byte value)
    {
        _length.WriteControl(value);

        if ((value & 0x80) != 0)
        {
            IsEnabled = _envelope.IsDACOn;
            _length.Restart();

            _timer = CyclesPerShift;
            _register = _is7Bit ? 0x40 : 0x4000;
            _envelope.Restart();
        }
    }

    public void Reset()
    {
        WriteLength(0);
        WriteEnvelope(0);
        WriteFrequency(0);
        WriteControl(0);
    }

    public void Advance(int cycles)
    {
        if (!IsEnabled)
        {
            return;
        }

        for (_timer -= cycles; _timer <= 0; _timer += CyclesPerShift)
        {
            _isHigh = (_register & 1) != 0;
            _register >>= 1;
            if (_isHigh)
            {
                _register ^= _is7Bit ? 0x60 : 0x6000;
            }
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
}
