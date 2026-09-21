namespace AGBModern.PSG;

internal struct Envelope
{
    private int _stepTime;
    private bool _increases;
    private int _initialVolume;
    private int _timer;

    public int Volume { get; private set; }

    public readonly bool IsDACOn => _initialVolume != 0 || _increases;

    public void Write(byte value)
    {
        _stepTime = value & 7;
        _increases = (value & 8) != 0;
        _initialVolume = value >> 4;
    }

    public void Restart()
    {
        Volume = _initialVolume;
        _timer = _stepTime;
    }

    public void Clock()
    {
        if (_stepTime == 0 || --_timer > 0)
        {
            return;
        }

        _timer = _stepTime;
        if (_increases && Volume < 15)
        {
            Volume++;
        }
        else if (!_increases && Volume > 0)
        {
            Volume--;
        }
    }
}
