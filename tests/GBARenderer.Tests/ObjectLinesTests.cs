using System.Buffers.Binary;

namespace GBARenderer.Tests;

public class ObjectLinesTests
{
    private readonly byte[] _oam = new byte[0x400];
    private readonly Displacement[] _displacements = new Displacement[FrameMotion.ObjectCount];
    private readonly uint[] _lines = new uint[FrameRenderer.Height * 28];
    private readonly uint[] _objectLines = new uint[FrameRenderer.Height * 4];

    public ObjectLinesTests()
    {
        for (int n = 0; n < FrameMotion.ObjectCount; n++)
        {
            SetObject(n, attribute0: 0x0200, attribute1: 0);
        }
    }

    private void SetObject(int n, ushort attribute0, ushort attribute1)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_oam.AsSpan(n * 8), attribute0);
        BinaryPrimitives.WriteUInt16LittleEndian(_oam.AsSpan((n * 8) + 2), attribute1);
    }

    private int[] LinesOf(int n)
    {
        FrameRenderer.FindObjectLines(_ => _oam, _displacements, _lines, _objectLines);
        return [.. Enumerable.Range(0, FrameRenderer.Height).Where(line => (_objectLines[(line * 4) + (n / 32)] & (1u << (n % 32))) != 0)];
    }

    [Fact]
    public void AnObjectIsOnTheLinesItCovers()
    {
        SetObject(40, attribute0: 150, attribute1: 0xC000);

        Assert.Equal(Enumerable.Range(150, 10), LinesOf(40));
    }

    [Fact]
    public void AnObjectPastTheBottomOfTheYRangeShowsAtTheTop()
    {
        SetObject(1, attribute0: 250, attribute1: 0);

        Assert.Equal(Enumerable.Range(0, 2), LinesOf(1));
    }

    [Fact]
    public void ADoubleSizeObject128PixelsTallBelowLine128ShowsOnlyAtTheTop()
    {
        SetObject(2, attribute0: 0x0300 | 140, attribute1: 0xC000);

        Assert.Equal(Enumerable.Range(0, 12), LinesOf(2));
    }

    [Fact]
    public void ADisabledObjectIsOnNoLines()
    {
        SetObject(3, attribute0: 0x0200 | 10, attribute1: 0);

        Assert.Empty(LinesOf(3));
    }

    [Fact]
    public void ObjectsPastTheLinesCycleBudgetAreNotDrawn()
    {
        for (int n = 0; n < 20; n++)
        {
            SetObject(n, attribute0: 0x0100 | 10, attribute1: 0xC000 | 16);
        }

        Assert.Equal(Enumerable.Range(10, 64), LinesOf(7));
        Assert.Empty(LinesOf(8));
    }

    [Fact]
    public void ANormalObjectTakesOneCyclePerPixelOfWidth()
    {
        for (int n = 0; n < 20; n++)
        {
            SetObject(n, attribute0: 0x0000 | 10, attribute1: 0xC000 | 16);
        }

        Assert.Equal(Enumerable.Range(10, 64), LinesOf(17));
        Assert.Empty(LinesOf(18));
        Assert.Empty(LinesOf(19));
    }

    [Fact]
    public void HBlankIntervalFreeLeavesFewerCycles()
    {
        for (int n = 0; n < 20; n++)
        {
            SetObject(n, attribute0: 0x0100 | 10, attribute1: 0xC000 | 16);
        }

        for (int line = 0; line < FrameRenderer.Height; line++)
        {
            _lines[line * 28] = 1 << 5;
        }

        Assert.Equal(Enumerable.Range(10, 64), LinesOf(5));
        Assert.Empty(LinesOf(6));
    }

    [Fact]
    public void ObjectsOffTheSidesOfTheScreenStillTakeTheirCycles()
    {
        for (int n = 0; n < 20; n++)
        {
            SetObject(n, attribute0: 0x0100 | 10, attribute1: 0xC000 | 300);
        }

        Assert.Empty(LinesOf(8));
    }

    [Fact]
    public void AMovingObjectCoversEveryLineItPassesThrough()
    {
        SetObject(4, attribute0: 20, attribute1: 0);
        _displacements[4] = new Displacement { From = new(0, -4.5f), To = new(0, 0) };

        Assert.Equal(Enumerable.Range(15, 13), LinesOf(4));
    }
}
