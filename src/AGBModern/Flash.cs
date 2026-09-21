namespace AGBModern;

internal static class Flash
{
    private const int SectorSize = 0x1000;

    private enum State
    {
        Ready,
        Unlocking,
        Unlocked,
        ProgramByte,
        SelectBank,
    }

    private static State _state;
    private static bool _isInIDMode;
    private static bool _isEraseArmed;
    private static int _bank;

    private static bool IsLarge => SaveMemory.Type == SaveType.Flash128K;

    public static void Reset()
    {
        _state = State.Ready;
        _isInIDMode = false;
        _isEraseArmed = false;
        _bank = 0;
    }

    public static byte Read(uint address)
    {
        int offset = (int)(address & 0xFFFF);
        if (_isInIDMode && offset < 2)
        {
            return (IsLarge, offset) switch
            {
                (true, 0) => 0xC2,  // Macronix
                (true, _) => 0x09,  // MX29L010
                (false, 0) => 0x32, // Panasonic
                (false, _) => 0x1B, // MN63F805MNP
            };
        }

        return SaveMemory.Data[(_bank << 16) | offset];
    }

    public static void Write(uint address, byte value)
    {
        int offset = (int)(address & 0xFFFF);

        switch (_state)
        {
            case State.ProgramByte:
                SaveMemory.Data[(_bank << 16) | offset] &= value;
                SaveMemory.IsModified = true;
                _state = State.Ready;
                break;

            case State.SelectBank:
                if (offset == 0)
                {
                    _bank = value & 1;
                }

                _state = State.Ready;
                break;

            case State.Ready when offset == 0x5555 && value == 0xAA:
                _state = State.Unlocking;
                break;

            case State.Unlocking when offset == 0x2AAA && value == 0x55:
                _state = State.Unlocked;
                break;

            case State.Unlocked:
                _state = State.Ready;
                RunCommand(offset, value);
                break;

            default:
                _state = State.Ready;
                break;
        }
    }

    private static void RunCommand(int offset, byte command)
    {
        if (_isEraseArmed && command == 0x30)
        {
            SaveMemory.Data.Slice((_bank << 16) | (offset & ~(SectorSize - 1)), SectorSize).Fill(0xFF);
            SaveMemory.IsModified = true;
            _isEraseArmed = false;
            return;
        }

        if (offset != 0x5555)
        {
            return;
        }

        bool wasEraseArmed = _isEraseArmed;
        _isEraseArmed = false;

        switch (command)
        {
            case 0x90:
                _isInIDMode = true;
                break;
            case 0xF0:
                _isInIDMode = false;
                break;
            case 0x80:
                _isEraseArmed = true;
                break;
            case 0x10 when wasEraseArmed:
                SaveMemory.Data.Fill(0xFF);
                SaveMemory.IsModified = true;
                break;
            case 0xA0:
                _state = State.ProgramByte;
                break;
            case 0xB0 when IsLarge:
                _state = State.SelectBank;
                break;
        }
    }
}
