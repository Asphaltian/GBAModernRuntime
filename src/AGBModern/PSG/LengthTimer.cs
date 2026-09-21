namespace AGBModern.PSG;

internal struct LengthTimer(int start)
{
    private int _remaining;
    private bool _stopsAtEnd;

    public void Write(int value)
    {
        _remaining = start - value;
    }

    public void WriteControl(byte value)
    {
        _stopsAtEnd = (value & 0x40) != 0;
    }

    public void Restart()
    {
        if (_remaining == 0)
        {
            _remaining = start;
        }
    }

    public bool Clock()
    {
        return _stopsAtEnd && _remaining > 0 && --_remaining == 0;
    }
}
