namespace AGBModern;

/// <summary>The kinds of save chip a cartridge can have.</summary>
public enum SaveType
{
    None,
    SRAM,
    EEPROM512,
    EEPROM8K,
    Flash64K,
    Flash128K,
}

internal static class SaveMemory
{
    private const byte Erased = 0xFF;
    private const uint SRAMSize = 0x8000;

    private static byte[] _data = [];

    public static SaveType Type { get; private set; }

    public static bool IsModified { get; set; }

    internal static Span<byte> Data => _data;

    internal static bool IsEEPROM => Type is SaveType.EEPROM512 or SaveType.EEPROM8K;

    private static int SizeOf(SaveType type) => type switch
    {
        SaveType.SRAM => (int)SRAMSize,
        SaveType.EEPROM512 => 0x200,
        SaveType.EEPROM8K => 0x2000,
        SaveType.Flash64K => 0x10000,
        SaveType.Flash128K => 0x20000,
        _ => 0,
    };

    public static void Initialize(SaveType type)
    {
        Type = type;
        _data = new byte[SizeOf(type)];
        _data.AsSpan().Fill(Erased);
        IsModified = false;
        Flash.Reset();
        EEPROM.Reset();
    }

    public static void Load(ReadOnlySpan<byte> contents)
    {
        _data.AsSpan().Fill(Erased);
        contents[..Math.Min(contents.Length, _data.Length)].CopyTo(_data);
    }

    internal static byte Read8(uint address) => Type switch
    {
        SaveType.SRAM => _data[address & (SRAMSize - 1)],
        SaveType.Flash64K or SaveType.Flash128K => Flash.Read(address),
        _ => 0,
    };

    internal static void Write8(uint address, byte value)
    {
        switch (Type)
        {
            case SaveType.SRAM:
                _data[address & (SRAMSize - 1)] = value;
                IsModified = true;
                break;
            case SaveType.Flash64K or SaveType.Flash128K:
                Flash.Write(address, value);
                break;
        }
    }
}
