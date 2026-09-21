namespace AGBModern;

internal static class EEPROM
{
    private const int BlockBits = 64;
    private const int ReadLeadBits = 4;
    private const int WriteCycles = 108368;

    private enum State
    {
        Command,
        Address,
        Data,
        Stop,
    }

    private static State _state;
    private static int _bitCount;
    private static bool _isWrite;
    private static int _address;
    private static ulong _block;

    private static int _readBlock = -1;
    private static int _readPosition;
    private static long _busyUntil;

    private static int AddressBits => SaveMemory.Type == SaveType.EEPROM512 ? 6 : 14;

    private static int BlockMask => (SaveMemory.Data.Length / 8) - 1;

    public static void Reset()
    {
        _state = State.Command;
        _bitCount = 0;
        _readBlock = -1;
        _busyUntil = 0;
    }

    public static ushort Read()
    {
        if (_readBlock < 0)
        {
            return Scheduler.Cycles >= _busyUntil ? (ushort)1 : (ushort)0;
        }

        int block = _readBlock;
        int position = _readPosition++;
        if (_readPosition == ReadLeadBits + BlockBits)
        {
            _readBlock = -1;
        }

        if (position < ReadLeadBits)
        {
            return 0;
        }

        int bit = position - ReadLeadBits;
        return (ushort)((SaveMemory.Data[(block * 8) + (bit / 8)] >> (7 - (bit % 8))) & 1);
    }

    public static void Write(ushort value)
    {
        uint bit = value & 1u;
        _bitCount++;

        switch (_state)
        {
            case State.Command when _bitCount == 1:
                if (bit == 0)
                {
                    _bitCount = 0;
                }

                break;

            case State.Command:
                _isWrite = bit == 0;
                _address = 0;
                Next(State.Address);
                break;

            case State.Address:
                _address = (_address << 1) | (int)bit;
                if (_bitCount == AddressBits)
                {
                    _block = 0;
                    Next(_isWrite ? State.Data : State.Stop);
                }

                break;

            case State.Data:
                _block = (_block << 1) | bit;
                if (_bitCount == BlockBits)
                {
                    Next(State.Stop);
                }

                break;

            case State.Stop:
                Finish();
                Next(State.Command);
                break;
        }
    }

    private static void Next(State state)
    {
        _state = state;
        _bitCount = 0;
    }

    private static void Finish()
    {
        int block = _address & BlockMask;
        if (!_isWrite)
        {
            _readBlock = block;
            _readPosition = 0;
            return;
        }

        for (int i = 0; i < 8; i++)
        {
            SaveMemory.Data[(block * 8) + i] = (byte)(_block >> (56 - (8 * i)));
        }

        SaveMemory.IsModified = true;
        _busyUntil = Scheduler.Cycles + WriteCycles;
    }
}
