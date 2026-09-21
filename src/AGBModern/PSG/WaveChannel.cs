namespace AGBModern.PSG;

internal sealed class WaveChannel
{
    private const int DigitsPerBank = 32;

    private readonly byte[] _waveRAM = new byte[32];
    private readonly int[] _shifted = new int[2];

    private bool _usesBothBanks;
    private int _selectedBank;
    private int _secondBankPlaying;
    private bool _isPlaybackOn;
    private int _volumeQuarters;
    private int _frequency;
    private int _timer;
    private int _played;
    private LengthTimer _length = new(256);

    public bool IsEnabled { get; private set; }

    public int Output
    {
        get
        {
            if (!IsEnabled)
            {
                return 0;
            }

            int centered = (Digit(PlayingBank, 0) * 2) - 15;
            return centered * _volumeQuarters / 4;
        }
    }

    private int PlayingBank => _selectedBank ^ _secondBankPlaying;

    private int CyclesPerSample => (2048 - _frequency) * 8;

    public void WriteControl(byte value)
    {
        _usesBothBanks = (value & 0x20) != 0;
        _selectedBank = (value >> 6) & 1;
        _isPlaybackOn = (value & 0x80) != 0;
        IsEnabled &= _isPlaybackOn;
    }

    public void WriteLength(byte value)
    {
        _length.Write(value);
    }

    public void WriteVolume(byte value)
    {
        _volumeQuarters = (value & 0x80) != 0 ? 3 : ((value >> 5) & 3) switch
        {
            0 => 0,
            1 => 4,
            2 => 2,
            _ => 1,
        };
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
            IsEnabled = _isPlaybackOn;
            _length.Restart();

            _timer = CyclesPerSample;
            _played = 0;
            _secondBankPlaying = 0;
        }
    }

    public byte ReadWaveRAM(int offset)
    {
        int bank = 1 - _selectedBank;
        return (byte)((Digit(bank, offset * 2) << 4) | Digit(bank, (offset * 2) + 1));
    }

    public void WriteWaveRAM(int offset, byte value)
    {
        int bank = 1 - _selectedBank;
        SetDigit(bank, offset * 2, value >> 4);
        SetDigit(bank, (offset * 2) + 1, value & 15);
    }

    public void ClearWaveRAM() => Array.Clear(_waveRAM);

    public void Reset()
    {
        WriteControl(0);
        WriteLength(0);
        WriteVolume(0);
        WriteFrequencyLow(0);
        WriteFrequencyHigh(0);
    }

    public void Advance(int cycles)
    {
        if (!IsEnabled)
        {
            return;
        }

        for (_timer -= cycles; _timer <= 0; _timer += CyclesPerSample)
        {
            int bank = PlayingBank;
            _shifted[bank] = (_shifted[bank] + 1) % DigitsPerBank;
            if (++_played == DigitsPerBank)
            {
                _played = 0;
                _secondBankPlaying = _usesBothBanks ? _secondBankPlaying ^ 1 : 0;
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

    private int Digit(int bank, int index)
    {
        int digit = (index + _shifted[bank]) % DigitsPerBank;
        return (_waveRAM[(bank * 16) + (digit >> 1)] >> ((digit & 1) == 0 ? 4 : 0)) & 15;
    }

    private void SetDigit(int bank, int index, int value)
    {
        int digit = (index + _shifted[bank]) % DigitsPerBank;
        ref byte pair = ref _waveRAM[(bank * 16) + (digit >> 1)];
        pair = (digit & 1) == 0 ? (byte)((pair & 0x0F) | (value << 4)) : (byte)((pair & 0xF0) | value);
    }
}
