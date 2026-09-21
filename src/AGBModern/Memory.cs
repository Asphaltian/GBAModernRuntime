using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AGBModern;

/// <summary>
/// The GBA's memory. Keep in mind that Read and Write cost cycles just like the game's own accesses
/// do, so in your own code you'll usually want <see cref="Peek{T}"/>, <see cref="Poke{T}"/> and
/// <see cref="TryGetSpan"/> instead, since they don't.
/// </summary>
public static class Memory
{
    private const uint ExtendedRAMAddress = 0x01000000;
    private const uint BIOSSize = 0x4000;
    private const uint WRAMDisable = 1 << 0;
    private const uint EWRAMEnable = 1 << 5;
    private const int LargeROMSize = 0x1000000;
    private const uint EEPROMStart = 0xFFFF00;
    private const uint TiledBGVRAMEnd = 0x10000;
    private const uint BitmapBGVRAMEnd = 0x14000;
    private const int FirstBitmapMode = 3;
    private const ushort PrefetchEnable = 1 << 14;

    public static readonly byte[] EWRAM = new byte[0x40000];
    public static readonly byte[] IWRAM = new byte[0x8000];
    public static readonly byte[] PaletteRAM = new byte[0x400];
    public static readonly byte[] VRAM = new byte[0x18000];
    public static readonly byte[] OAM = new byte[0x400];
    public static readonly byte[] ROM = new byte[0x2000000];

    /// <summary>
    /// Extra memory at 0x01000000 that the real GBA doesn't have. Use it when your mod needs the game
    /// to read something of yours, and grab a piece of it with <see cref="Allocate"/>.
    /// </summary>
    public static readonly byte[] ExtendedRAM = new byte[0x100000];

    private static readonly SortedList<int, int> FreeBlocks = new() { [0] = ExtendedRAM.Length };
    private static readonly Dictionary<int, int> AllocatedBlocks = [];
    private static readonly byte[] CyclesPerHalfword = new byte[256];
    private static readonly int[] SequentialSaving = new int[256];
    private static readonly byte[] CyclesPerWord = CreateWordTimings(CyclesPerHalfword);

    private static int _romSize;
    private static bool _isWRAMNormal = true;
    private static bool _isWRAMEnabled = true;

    internal static uint BIOSOpcode { get; set; }

    internal static bool IsPrefetchEnabled { get; private set; }

    internal static int ROMNonSequential => CyclesPerHalfword[0x8];

    internal static int ROMSequential => CyclesPerWord[0x8] - CyclesPerHalfword[0x8];

    internal static int EWRAMHalfword => CyclesPerHalfword[0x2];

    internal static void LoadROM(ReadOnlySpan<byte> rom)
    {
        if (rom.Length is 0 or > 0x2000000 || (rom.Length & 1) != 0)
        {
            throw new ArgumentException("A ROM is at most 32 MiB and an even number of bytes long.", nameof(rom));
        }

        rom.CopyTo(ROM);
        _romSize = rom.Length;

        for (int offset = rom.Length; offset < ROM.Length; offset += 2)
        {
            Unsafe.WriteUnaligned(ref ROM[offset], (ushort)(offset >> 1));
        }
    }

    /// <summary>
    /// Reads the way the game does, cycles and all. You can leave <paramref name="openBus"/> out: it's
    /// what an address with nothing behind it reads as, and recompiled code fills it in.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Read32(uint address, uint openBus = 0)
    {
        Scheduler.Cycles += CyclesPerWord[address >> 24];
        return (address >> 24) switch
        {
            0x2 when _isWRAMNormal => Unsafe.ReadUnaligned<uint>(ref EWRAM[address & 0x3FFFC]),
            0x3 when _isWRAMNormal => Unsafe.ReadUnaligned<uint>(ref IWRAM[address & 0x7FFC]),
            >= 0x8 and <= 0xC => Unsafe.ReadUnaligned<uint>(ref ROM[address & 0x1FFFFFC]),
            _ => ReadSlow(address, openBus, 4),
        };
    }

    /// <inheritdoc cref="Read32"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Read16(uint address, uint openBus = 0)
    {
        Scheduler.Cycles += CyclesPerHalfword[address >> 24];
        return (address >> 24) switch
        {
            0x2 when _isWRAMNormal => Unsafe.ReadUnaligned<ushort>(ref EWRAM[address & 0x3FFFE]),
            0x3 when _isWRAMNormal => Unsafe.ReadUnaligned<ushort>(ref IWRAM[address & 0x7FFE]),
            >= 0x8 and <= 0xC => Unsafe.ReadUnaligned<ushort>(ref ROM[address & 0x1FFFFFE]),
            _ => (ushort)(ReadSlow(address, openBus, 2) >> (int)((address & 2) * 8)),
        };
    }

    /// <inheritdoc cref="Read32"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Read8(uint address, uint openBus = 0)
    {
        Scheduler.Cycles += CyclesPerHalfword[address >> 24];
        return (address >> 24) switch
        {
            0x2 when _isWRAMNormal => EWRAM[address & 0x3FFFF],
            0x3 when _isWRAMNormal => IWRAM[address & 0x7FFF],
            >= 0x8 and <= 0xC => ROM[address & 0x1FFFFFF],
            _ => (byte)(ReadSlow(address, openBus, 1) >> (int)((address & 3) * 8)),
        };
    }

    /// <summary>Writes the way the game does, cycles and all.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write32(uint address, uint value)
    {
        Scheduler.Cycles += CyclesPerWord[address >> 24];
        switch (address >> 24)
        {
            case 0x2 when _isWRAMNormal:
                Unsafe.WriteUnaligned(ref EWRAM[address & 0x3FFFC], value);
                break;
            case 0x3 when _isWRAMNormal:
                Unsafe.WriteUnaligned(ref IWRAM[address & 0x7FFC], value);
                break;
            default:
                Write32Slow(address, value);
                break;
        }
    }

    /// <inheritdoc cref="Write32"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write16(uint address, ushort value)
    {
        Scheduler.Cycles += CyclesPerHalfword[address >> 24];
        switch (address >> 24)
        {
            case 0x2 when _isWRAMNormal:
                Unsafe.WriteUnaligned(ref EWRAM[address & 0x3FFFE], value);
                break;
            case 0x3 when _isWRAMNormal:
                Unsafe.WriteUnaligned(ref IWRAM[address & 0x7FFE], value);
                break;
            default:
                Write16Slow(address, value);
                break;
        }
    }

    /// <inheritdoc cref="Write32"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write8(uint address, byte value)
    {
        Scheduler.Cycles += CyclesPerHalfword[address >> 24];
        switch (address >> 24)
        {
            case 0x2 when _isWRAMNormal:
                EWRAM[address & 0x3FFFF] = value;
                break;
            case 0x3 when _isWRAMNormal:
                IWRAM[address & 0x7FFF] = value;
                break;
            default:
                Write8Slow(address, value);
                break;
        }
    }

    private static uint ReadSlow(uint address, uint openBus, int width)
    {
        uint aligned = address & ~3u;
        switch (address >> 24)
        {
            case 0x0 when address < BIOSSize:
                return BIOSOpcode;
            case 0x1:
                return Unsafe.ReadUnaligned<uint>(ref ExtendedRAM[aligned & 0xFFFFC]);
            case 0x2 or 0x3 when !_isWRAMEnabled:
                return openBus;
            case 0x2 or 0x3:
                return Unsafe.ReadUnaligned<uint>(ref IWRAM[aligned & 0x7FFC]);
            case 0x4 when width == 4:
                return IO.Read16(aligned, openBus) | ((uint)IO.Read16(aligned + 2, openBus) << 16);
            case 0x4:
                return (uint)IO.Read16(address, openBus) * 0x10001;
            case 0x5:
                return Unsafe.ReadUnaligned<uint>(ref PaletteRAM[aligned & 0x3FC]);
            case 0x6:
                return Unsafe.ReadUnaligned<uint>(ref VRAM[VRAMOffset(aligned)]);
            case 0x7:
                return Unsafe.ReadUnaligned<uint>(ref OAM[aligned & 0x3FC]);
            case 0xD when width == 2 && IsEEPROM(address):
                return EEPROM.Read() * 0x10001u;
            case 0xD:
                return Unsafe.ReadUnaligned<uint>(ref ROM[aligned & 0x1FFFFFC]);
            case 0xE or 0xF:
                return SaveMemory.Read8(address) * 0x01010101u;
            default:
                return openBus;
        }
    }

    private static void Write32Slow(uint address, uint value)
    {
        uint aligned = address & ~3u;
        switch (address >> 24)
        {
            case 0x1:
                Unsafe.WriteUnaligned(ref ExtendedRAM[aligned & 0xFFFFC], value);
                break;
            case 0x2 or 0x3 when _isWRAMEnabled:
                Unsafe.WriteUnaligned(ref IWRAM[aligned & 0x7FFC], value);
                break;
            case 0x4:
                IO.Write16(aligned, (ushort)value);
                IO.Write16(aligned + 2, (ushort)(value >> 16));
                break;
            case 0x5:
                Video.BeforeVideoMemoryWrite(VideoFrame.PaletteRAMKilobyte);
                Unsafe.WriteUnaligned(ref PaletteRAM[aligned & 0x3FC], value);
                break;
            case 0x6:
                Video.BeforeVideoMemoryWrite(VRAMKilobyte(aligned));
                Unsafe.WriteUnaligned(ref VRAM[VRAMOffset(aligned)], value);
                break;
            case 0x7:
                Video.BeforeVideoMemoryWrite(VideoFrame.OAMKilobyte);
                Unsafe.WriteUnaligned(ref OAM[aligned & 0x3FC], value);
                break;
            case >= 0x8 and <= 0xD when GPIO.Covers(aligned):
                GPIO.Write16(aligned, (ushort)value);
                GPIO.Write16(aligned + 2, (ushort)(value >> 16));
                break;
            case 0xE or 0xF:
                SaveMemory.Write8(address, (byte)BitOperations.RotateRight(value, (int)(address & 3) * 8));
                break;
        }
    }

    private static void Write16Slow(uint address, ushort value)
    {
        uint aligned = address & ~1u;
        switch (address >> 24)
        {
            case 0x1:
                Unsafe.WriteUnaligned(ref ExtendedRAM[aligned & 0xFFFFE], value);
                break;
            case 0x2 or 0x3 when _isWRAMEnabled:
                Unsafe.WriteUnaligned(ref IWRAM[aligned & 0x7FFE], value);
                break;
            case 0x4:
                IO.Write16(aligned, value);
                break;
            case 0x5:
                Video.BeforeVideoMemoryWrite(VideoFrame.PaletteRAMKilobyte);
                Unsafe.WriteUnaligned(ref PaletteRAM[aligned & 0x3FE], value);
                break;
            case 0x6:
                Video.BeforeVideoMemoryWrite(VRAMKilobyte(aligned));
                Unsafe.WriteUnaligned(ref VRAM[VRAMOffset(aligned)], value);
                break;
            case 0x7:
                Video.BeforeVideoMemoryWrite(VideoFrame.OAMKilobyte);
                Unsafe.WriteUnaligned(ref OAM[aligned & 0x3FE], value);
                break;
            case >= 0x8 and <= 0xD when GPIO.Covers(aligned):
                GPIO.Write16(aligned, value);
                break;
            case 0xD when IsEEPROM(address):
                EEPROM.Write(value);
                break;
            case 0xE or 0xF:
                SaveMemory.Write8(address, (byte)(value >> (int)((address & 1) * 8)));
                break;
        }
    }

    private static void Write8Slow(uint address, byte value)
    {
        switch (address >> 24)
        {
            case 0x1:
                ExtendedRAM[address & 0xFFFFF] = value;
                break;
            case 0x2 or 0x3 when _isWRAMEnabled:
                IWRAM[address & 0x7FFF] = value;
                break;
            case 0x4:
                IO.Write8(address, value);
                break;
            case 0x5:
                Video.BeforeVideoMemoryWrite(VideoFrame.PaletteRAMKilobyte);
                Unsafe.WriteUnaligned(ref PaletteRAM[address & 0x3FE], (ushort)(value * 0x0101));
                break;
            case 0x6 when VRAMOffset(address) < BGVRAMEnd(IO.Read16(IO.DISPCNT) & 7):
                Video.BeforeVideoMemoryWrite(VRAMKilobyte(address));
                Unsafe.WriteUnaligned(ref VRAM[VRAMOffset(address) & ~1u], (ushort)(value * 0x0101));
                break;
            case 0xE or 0xF:
                SaveMemory.Write8(address, value);
                break;
        }
    }

    private static uint BGVRAMEnd(int mode) => mode >= FirstBitmapMode ? BitmapBGVRAMEnd : TiledBGVRAMEnd;

    private static bool IsEEPROM(uint address)
    {
        return address >> 24 == 0xD && SaveMemory.IsEEPROM && (_romSize <= LargeROMSize || (address & 0xFFFFFF) >= EEPROMStart);
    }

    /// <summary>
    /// Gives you a piece of <see cref="ExtendedRAM"/> and returns its address, which is always a
    /// multiple of 4. It's yours until you give it back with <see cref="Free"/>.
    /// </summary>
    public static uint Allocate(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        int needed = Math.Max(4, (size + 3) & ~3);

        int offset = -1;
        foreach (var (start, length) in FreeBlocks)
        {
            if (length >= needed)
            {
                offset = start;
                break;
            }
        }

        if (offset < 0)
        {
            throw new OutOfMemoryException($"No {size} bytes of extended RAM are free in one piece.");
        }

        int free = FreeBlocks[offset];
        FreeBlocks.Remove(offset);
        if (free > needed)
        {
            FreeBlocks.Add(offset + needed, free - needed);
        }

        AllocatedBlocks.Add(offset, needed);
        return ExtendedRAMAddress + (uint)offset;
    }

    /// <summary>Gives back a piece that <see cref="Allocate"/> gave you.</summary>
    public static void Free(uint address)
    {
        int offset = (int)(address - ExtendedRAMAddress);
        if (!AllocatedBlocks.Remove(offset, out int length))
        {
            throw new ArgumentException($"0x{address:X8} is not allocated extended RAM.", nameof(address));
        }

        if (FreeBlocks.Remove(offset + length, out int next))
        {
            length += next;
        }

        int previous = FreeBlocks.Keys.LastOrDefault(start => start < offset, -1);
        if (previous >= 0 && previous + FreeBlocks[previous] == offset)
        {
            FreeBlocks[previous] += length;
            return;
        }

        FreeBlocks.Add(offset, length);
    }

    /// <summary>Gets a run of bytes in RAM or ROM without costing any cycles. Returns false if the run doesn't fit inside one of them.</summary>
    public static bool TryGetSpan(uint address, int length, out ReadOnlySpan<byte> span)
    {
        (byte[]? region, uint mask) = (address >> 24) switch
        {
            0x1 => (ExtendedRAM, 0xFFFFFu),
            0x2 => (EWRAM, 0x3FFFFu),
            0x3 => (IWRAM, 0x7FFFu),
            >= 0x8 and <= 0xD => (ROM, 0x1FFFFFFu),
            _ => (null, 0u),
        };

        int offset = (int)(address & mask);
        if (region is null || offset + length > region.Length)
        {
            span = default;
            return false;
        }

        span = region.AsSpan(offset, length);
        return true;
    }

    /// <summary>Reads a struct straight out of RAM or ROM, without costing any cycles. Throws if it isn't inside one of them.</summary>
    /// <example>
    /// <code>
    /// // The game keeps a pointer here, so follow it to the struct behind it
    /// uint player = Memory.Peek&lt;uint&gt;(0x03000100);
    /// var position = Memory.Peek&lt;Position&gt;(player + 0x0C);
    /// </code>
    /// </example>
    public static ref readonly T Peek<T>(uint address)
        where T : unmanaged
    {
        if (!TryGetSpan(address, Unsafe.SizeOf<T>(), out var span))
        {
            throw new ArgumentOutOfRangeException(nameof(address), $"0x{address:X8} is not in RAM or ROM.");
        }

        return ref MemoryMarshal.AsRef<T>(span);
    }

    /// <summary>Gives you a struct in RAM to change in place, without costing any cycles. Throws if it isn't in RAM.</summary>
    /// <example>
    /// <code>
    /// // Top up the player's money
    /// Memory.Poke&lt;int&gt;(0x02001000) = 99999;
    /// </code>
    /// </example>
    public static ref T Poke<T>(uint address)
        where T : unmanaged
    {
        byte[]? region = (address >> 24) switch
        {
            0x1 => ExtendedRAM,
            0x2 => EWRAM,
            0x3 => IWRAM,
            _ => null,
        };

        int offset = (int)(address & (uint)((region?.Length ?? 1) - 1));
        if (region is null || offset + Unsafe.SizeOf<T>() > region.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(address), $"0x{address:X8} is not in RAM.");
        }

        return ref MemoryMarshal.AsRef<T>(region.AsSpan(offset));
    }

    internal static void SetWaitstates(ushort waitcnt, uint memoryControl)
    {
        FillTimings(CyclesPerHalfword, CyclesPerWord, waitcnt, memoryControl);
        IsPrefetchEnabled = (waitcnt & PrefetchEnable) != 0;
        _isWRAMEnabled = (memoryControl & WRAMDisable) == 0;
        _isWRAMNormal = _isWRAMEnabled && (memoryControl & EWRAMEnable) != 0;
    }

    internal static int SequentialSavingAt(uint address) => SequentialSaving[address >> 24];

    internal static int JumpCycles(uint target, bool isThumb)
    {
        int region = (int)(target >> 24);
        int nonSequential = isThumb ? CyclesPerHalfword[region] : CyclesPerWord[region];
        int sequential = region is >= 0x8 and <= 0xD
            ? (CyclesPerWord[region] - CyclesPerHalfword[region]) * (isThumb ? 1 : 2)
            : nonSequential;
        return nonSequential + sequential;
    }

    private static byte[] CreateWordTimings(byte[] halfword)
    {
        byte[] word = new byte[256];
        FillTimings(halfword, word, 0, IO.MemoryControlAtPowerOn);
        return word;
    }

    private static void FillTimings(Span<byte> halfword, Span<byte> word, ushort waitcnt, uint memoryControl)
    {
        uint ewramSetting = (memoryControl >> 24) & 0xF;
        if (ewramSetting == 15)
        {
            throw new InvalidOperationException("The game set 256K WRAM to wait control 15, which locks the GBA up.");
        }

        int ewram = 1 + 15 - (int)ewramSetting;
        int[] firstAccess = [5, 4, 3, 9];
        int sram = firstAccess[waitcnt & 3];

        halfword.Fill(1);
        word.Fill(1);
        (halfword[0x2], word[0x2]) = ((byte)ewram, (byte)(2 * ewram));
        word[0x5] = word[0x6] = 2;

        for (int state = 0; state < 3; state++)
        {
            int n = firstAccess[(waitcnt >> (2 + (state * 3))) & 3];
            int s = (waitcnt & (1 << (4 + (state * 3)))) != 0 ? 2 : 1 + (2 << state);
            halfword[0x8 + (state * 2)] = halfword[0x9 + (state * 2)] = (byte)n;
            word[0x8 + (state * 2)] = word[0x9 + (state * 2)] = (byte)(n + s);
            SequentialSaving[0x8 + (state * 2)] = SequentialSaving[0x9 + (state * 2)] = n - s;
        }

        halfword[0xE] = halfword[0xF] = word[0xE] = word[0xF] = (byte)sram;
    }

    private static int VRAMKilobyte(uint address) => (int)(VRAMOffset(address) / VideoFrame.KilobyteSize);

    private static uint VRAMOffset(uint address)
    {
        uint offset = address & 0x1FFFF;
        return offset >= 0x18000 ? offset & 0x17FFF : offset;
    }
}
