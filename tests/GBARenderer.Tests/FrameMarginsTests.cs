namespace GBARenderer.Tests;

public class FrameMarginsTests
{
    [Fact]
    public void ColumnsAreKeptFromTheScreenOutwardsOnEachSide()
    {
        var margins = new FrameMargins();
        margins.Supply(bg: 3, column: 0)[0] = 0x1111;
        margins.Supply(bg: 3, column: -1)[0] = 0x2222;
        margins.Supply(bg: 3, column: 30)[5] = 0x3333;

        int first = 3 * 2 * FrameMargins.ColumnsPerSide * FrameMargins.Rows;
        Assert.Equal(0x1111, margins.Tiles[first]);
        Assert.Equal(0x2222, margins.Tiles[first + FrameMargins.Rows]);
        Assert.Equal(0x3333, margins.Tiles[first + (FrameMargins.ColumnsPerSide * FrameMargins.Rows) + 5]);
        Assert.Equal((1u << FrameMargins.ColumnsPerSide) | 0b11u, margins.SuppliedColumns[3]);

        margins.Clear();
        Assert.Equal(0u, margins.SuppliedColumns[3]);
    }

    [Fact]
    public void WindowEdgesAreKeptPerLineUntilCleared()
    {
        var margins = new FrameMargins();
        Assert.All(margins.WindowEdges, edges => Assert.Equal(FrameMargins.NoWindow, edges));

        margins.SupplyWindow(window: 1, line: 10, x1: -30, x2: 250);
        Assert.Equal(0xFFE2u | (250u << 16), margins.WindowEdges[160 + 10]);

        margins.Clear();
        Assert.Equal(FrameMargins.NoWindow, margins.WindowEdges[160 + 10]);
    }

    [Fact]
    public void TheWidthStaysWithinWhatTheColumnsCanCover()
    {
        var margins = new FrameMargins { Width = 500 };
        Assert.Equal(FrameMargins.MaxWidth, margins.Width);

        margins.Width = -3;
        Assert.Equal(0, margins.Width);
    }
}
