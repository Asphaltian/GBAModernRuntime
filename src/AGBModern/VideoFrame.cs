using System.Runtime.InteropServices;

namespace AGBModern;

/// <summary>Everything you need to draw one frame. <see cref="Video.FrameFinished"/> gives you these.</summary>
public sealed class VideoFrame
{
    /// <summary>
    /// How many words each line takes in <see cref="Lines"/>: the display registers from 0x04000000 to
    /// 0x0400005F, then the BG2 and BG3 reference points for that line.
    /// </summary>
    public const int WordsPerLine = 28;

    internal const int KilobyteSize = 0x400;
    internal const int VideoMemoryKilobytes = 98;
    internal const int PaletteRAMKilobyte = 96;
    internal const int OAMKilobyte = 97;

    private const int VRAMSize = 0x18000;

    /// <summary>The display registers as they were on each of the 160 visible lines.</summary>
    public readonly uint[] Lines = new uint[Video.VisibleLines * WordsPerLine];

    internal readonly byte[] LayoutOfLine = new byte[Video.VisibleLines];

    private readonly List<EarlierCopy> _earlierCopies = [];
    private readonly int[] _copiedAt = new int[VideoMemoryKilobytes];
    private int _linesCaptured;

    /// <summary>VRAM as it was when the frame finished.</summary>
    public ReadOnlySpan<byte> VRAM => Copies.AsSpan(0, VRAMSize);

    /// <summary>Palette RAM as it was when the frame finished.</summary>
    public ReadOnlySpan<byte> PaletteRAM => Copies.AsSpan(PaletteRAMKilobyte * KilobyteSize, KilobyteSize);

    /// <summary>OAM as it was when the frame finished.</summary>
    public ReadOnlySpan<byte> OAM => Copies.AsSpan(OAMKilobyte * KilobyteSize, KilobyteSize);

    internal byte[] Copies { get; private set; } = new byte[VideoMemoryKilobytes * KilobyteSize];

    internal int CopyCount { get; private set; } = VideoMemoryKilobytes;

    internal ushort[] Layouts { get; private set; } = [.. Enumerable.Range(0, VideoMemoryKilobytes).Select(kilobyte => (ushort)kilobyte)];

    internal int LayoutCount { get; private set; } = 1;

    internal void CaptureLine(int line, ReadOnlySpan<int> referencePoints)
    {
        if (line == 0)
        {
            _earlierCopies.Clear();
            Array.Clear(_copiedAt);
            CopyCount = VideoMemoryKilobytes;
        }

        var words = Lines.AsSpan(line * WordsPerLine, WordsPerLine);
        for (int i = 0; i < 24; i++)
        {
            uint offset = (uint)i * 4;
            words[i] = IO.ReadRegister(offset) | ((uint)IO.ReadRegister(offset + 2) << 16);
        }

        MemoryMarshal.Cast<int, uint>(referencePoints).CopyTo(words[24..]);
        _linesCaptured = line + 1;
    }

    internal void CopyBeforeWrite(int kilobyte)
    {
        if (_linesCaptured == 0 || _copiedAt[kilobyte] == _linesCaptured)
        {
            return;
        }

        _copiedAt[kilobyte] = _linesCaptured;
        if ((CopyCount + 1) * KilobyteSize > Copies.Length)
        {
            Copies = [.. Copies, .. new byte[Copies.Length]];
        }

        VideoMemory(kilobyte).CopyTo(Copies.AsSpan(CopyCount * KilobyteSize));
        _earlierCopies.Add(new EarlierCopy(kilobyte, _linesCaptured, CopyCount));
        CopyCount++;
    }

    internal void CaptureMemory()
    {
        for (int kilobyte = 0; kilobyte < VideoMemoryKilobytes; kilobyte++)
        {
            VideoMemory(kilobyte).CopyTo(Copies.AsSpan(kilobyte * KilobyteSize));
        }

        Span<ushort> layout = stackalloc ushort[VideoMemoryKilobytes];
        for (int kilobyte = 0; kilobyte < VideoMemoryKilobytes; kilobyte++)
        {
            layout[kilobyte] = (ushort)kilobyte;
        }

        LayoutCount = 0;
        int next = _earlierCopies.Count - 1;
        for (int line = Video.VisibleLines - 1; line >= 0; line--)
        {
            bool changed = LayoutCount == 0;
            for (; next >= 0 && _earlierCopies[next].LinesBefore > line; next--)
            {
                layout[_earlierCopies[next].Kilobyte] = (ushort)_earlierCopies[next].Copy;
                changed = true;
            }

            if (changed)
            {
                if ((LayoutCount + 1) * VideoMemoryKilobytes > Layouts.Length)
                {
                    Layouts = [.. Layouts, .. new ushort[Layouts.Length]];
                }

                layout.CopyTo(Layouts.AsSpan(LayoutCount * VideoMemoryKilobytes));
                LayoutCount++;
            }

            LayoutOfLine[line] = (byte)(LayoutCount - 1);
        }

        _linesCaptured = 0;
    }

    internal ReadOnlySpan<byte> OnLine(int line, int kilobyte)
    {
        int copy = Layouts[(LayoutOfLine[line] * VideoMemoryKilobytes) + kilobyte];
        return Copies.AsSpan(copy * KilobyteSize, KilobyteSize);
    }

    private static Span<byte> VideoMemory(int kilobyte) => kilobyte switch
    {
        PaletteRAMKilobyte => Memory.PaletteRAM,
        OAMKilobyte => Memory.OAM,
        _ => Memory.VRAM.AsSpan(kilobyte * KilobyteSize, KilobyteSize),
    };

    private readonly record struct EarlierCopy(int Kilobyte, int LinesBefore, int Copy);
}
