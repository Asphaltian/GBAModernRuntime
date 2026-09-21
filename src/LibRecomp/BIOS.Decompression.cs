using AGBModern;

namespace LibRecomp;

internal static partial class BIOS
{
    private static void BitUnPack(RecompContext ctx, uint source, uint destination, uint info)
    {
        int length = Memory.Read16(info);
        int sourceWidth = Memory.Read8(info + 2);
        int destinationWidth = Memory.Read8(info + 3);
        uint offset = Memory.Read32(info + 4);
        bool offsetZeros = (offset & 0x80000000) != 0;
        offset &= 0x7FFFFFFF;

        uint sourceMask = (1u << sourceWidth) - 1;
        uint destinationMask = destinationWidth == 32 ? uint.MaxValue : (1u << destinationWidth) - 1;
        uint word = 0;
        int bits = 0;

        for (int i = 0; i < length; i++)
        {
            byte value = Memory.Read8(source + (uint)i);
            for (int shift = 0; shift < 8; shift += sourceWidth)
            {
                uint unit = (uint)(value >> shift) & sourceMask;
                if (unit != 0 || offsetZeros)
                {
                    unit += offset;
                }

                word |= (unit & destinationMask) << bits;
                bits += destinationWidth;
                if (bits == 32)
                {
                    Memory.Write32(destination, word);
                    destination += 4;
                    word = 0;
                    bits = 0;
                    CheckEvents(ctx);
                }
            }
        }
    }

    private static void LZ77UnComp(uint source, Output output)
    {
        int remaining = (int)(Memory.Read32(source) >> 8);
        source += 4;

        while (remaining > 0)
        {
            byte flags = Memory.Read8(source++);
            for (int block = 0; block < 8 && remaining > 0; block++, flags <<= 1)
            {
                if ((flags & 0x80) == 0)
                {
                    output.Write(Memory.Read8(source++));
                    remaining--;
                    continue;
                }

                byte first = Memory.Read8(source++);
                byte second = Memory.Read8(source++);
                int count = (first >> 4) + 3;
                uint distance = (uint)(((first & 0xF) << 8) | second) + 1;
                for (; count > 0 && remaining > 0; count--, remaining--)
                {
                    output.Write(Memory.Read8(output.Address - distance));
                }
            }
        }
    }

    private static void HuffUnComp(RecompContext ctx, uint source, uint destination)
    {
        uint header = Memory.Read32(source);
        int dataBits = (int)(header & 0xF);
        uint size = header >> 8;
        uint root = source + 5;
        uint bitstream = source + 4 + ((uint)(Memory.Read8(source + 4) + 1) * 2);

        uint node = root;
        uint word = 0;
        int wordBits = 0;
        uint written = 0;

        while (written < size)
        {
            uint bits = Memory.Read32(bitstream);
            bitstream += 4;
            for (int bit = 31; bit >= 0 && written < size; bit--)
            {
                byte value = Memory.Read8(node);
                int direction = (int)((bits >> bit) & 1);
                uint child = (node & ~1u) + ((uint)(value & 0x3F) * 2) + 2 + (uint)direction;
                bool isData = (value & (direction == 0 ? 0x80 : 0x40)) != 0;
                if (!isData)
                {
                    node = child;
                    continue;
                }

                word |= (uint)Memory.Read8(child) << wordBits;
                wordBits += dataBits;
                node = root;
                if (wordBits == 32)
                {
                    Memory.Write32(destination + written, word);
                    written += 4;
                    word = 0;
                    wordBits = 0;
                    CheckEvents(ctx);
                }
            }
        }
    }

    private static void RLUnComp(uint source, Output output)
    {
        int remaining = (int)(Memory.Read32(source) >> 8);
        source += 4;

        while (remaining > 0)
        {
            byte flag = Memory.Read8(source++);
            if ((flag & 0x80) != 0)
            {
                byte value = Memory.Read8(source++);
                for (int count = (flag & 0x7F) + 3; count > 0 && remaining > 0; count--, remaining--)
                {
                    output.Write(value);
                }
            }
            else
            {
                for (int count = (flag & 0x7F) + 1; count > 0 && remaining > 0; count--, remaining--)
                {
                    output.Write(Memory.Read8(source++));
                }
            }
        }
    }

    private static void Diff8bitUnFilter(uint source, Output output)
    {
        uint size = Memory.Read32(source) >> 8;
        byte value = 0;
        for (uint i = 0; i < size; i++)
        {
            value += Memory.Read8(source + 4 + i);
            output.Write(value);
        }
    }

    private static void Diff16bitUnFilter(RecompContext ctx, uint source, uint destination)
    {
        uint size = Memory.Read32(source) >> 8;
        ushort value = 0;
        for (uint i = 0; i < size; i += 2)
        {
            value += Memory.Read16(source + 4 + i);
            Memory.Write16(destination + i, value);
            CheckEvents(ctx);
        }
    }

    private sealed class Output(RecompContext ctx, uint address, bool halfwords)
    {
        private int _pending = -1;

        public uint Address { get; private set; } = address;

        public void Write(byte value)
        {
            if (!halfwords)
            {
                Memory.Write8(Address++, value);
                CheckEvents(ctx);
                return;
            }

            if (_pending < 0)
            {
                _pending = value;
            }
            else
            {
                Memory.Write16(Address - 1, (ushort)(_pending | (value << 8)));
                _pending = -1;
                CheckEvents(ctx);
            }

            Address++;
        }
    }
}
