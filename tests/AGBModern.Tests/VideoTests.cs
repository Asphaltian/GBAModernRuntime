namespace AGBModern.Tests;

public class VideoTests
{
    private const int CyclesPerFrame = 280896;

    [Fact]
    public void AFrameKeepsItsLengthWhenHBlankDMATakesCycles()
    {
        var frameTimes = new List<long>();
        void OnFrame(VideoFrame frame) => frameTimes.Add(Scheduler.Cycles);

        IO.Write16(0x0B0, 0x3600);
        IO.Write16(0x0B2, 0x0200);
        IO.Write16(0x0B4, 0x3700);
        IO.Write16(0x0B6, 0x0200);
        IO.Write16(0x0B8, 2);
        IO.Write16(0x0BA, 0xA240);

        Video.FrameFinished += OnFrame;
        Video.Start(paced: false);
        for (int i = 0; i < 4 * CyclesPerFrame / 64; i++)
        {
            Scheduler.Cycles += 64;
            Scheduler.RunDueEvents();
        }

        Video.FrameFinished -= OnFrame;
        IO.Write16(0x0BA, 0);

        Assert.True(frameTimes.Count >= 3);
        Assert.InRange(frameTimes[2] - frameTimes[0], (2 * CyclesPerFrame) - 128, (2 * CyclesPerFrame) + 128);
    }

    [Fact]
    public void LinesDrawnBeforeAWriteToVideoMemoryKeepWhatWasThere()
    {
        VideoFrame? captured = null;
        void OnFrame(VideoFrame frame) => captured ??= frame;

        Memory.Write16(0x05000000, 0x1111);
        Memory.Write16(0x06000000, 0x2222);
        Video.FrameFinished += OnFrame;
        long start = Scheduler.Cycles;
        Video.Start(paced: false);
        for (int i = 0; i < CyclesPerFrame / 64 && captured is null; i++)
        {
            Scheduler.Cycles += 64;
            Scheduler.RunDueEvents();
            if (Scheduler.Cycles - start is >= (80 * 1232) + 1006 and < (80 * 1232) + 1006 + 64)
            {
                Memory.Write16(0x05000000, 0x3333);
                Memory.Write16(0x06000000, 0x4444);
            }
        }

        Video.FrameFinished -= OnFrame;
        Assert.Equal(0x1111, BitConverter.ToUInt16(captured!.OnLine(80, VideoFrame.PaletteRAMKilobyte)));
        Assert.Equal(0x3333, BitConverter.ToUInt16(captured.OnLine(81, VideoFrame.PaletteRAMKilobyte)));
        Assert.Equal(0x2222, BitConverter.ToUInt16(captured.OnLine(80, 0)));
        Assert.Equal(0x4444, BitConverter.ToUInt16(captured.OnLine(159, 0)));
        Assert.Equal(0x3333, BitConverter.ToUInt16(captured.PaletteRAM));
    }

    [Fact]
    public void DISPSTATFollowsTheLineAndRaisesItsInterrupts()
    {
        Memory.Write16(0x04000202, 0xFFFF);
        Memory.Write16(0x04000004, (80 << 8) | 0x38);
        long start = Scheduler.Cycles;
        Video.Start(paced: false);

        void RunUntil(long cycle)
        {
            while (Scheduler.Cycles - start < cycle)
            {
                Scheduler.Cycles += 16;
                Scheduler.RunDueEvents();
            }
        }

        RunUntil((80 * 1232) + 16);
        Assert.Equal(80, Memory.Read16(0x04000006));
        Assert.Equal(0x0004, Memory.Read16(0x04000004) & 7);
        Assert.NotEqual(0, Memory.Read16(0x04000202) & (ushort)Interrupt.VCount);

        RunUntil((80 * 1232) + 1006 + 16);
        Assert.Equal(0x0006, Memory.Read16(0x04000004) & 7);
        Assert.NotEqual(0, Memory.Read16(0x04000202) & (ushort)Interrupt.HBlank);

        RunUntil((160 * 1232) + 16);
        Assert.Equal(0x0001, Memory.Read16(0x04000004) & 7);
        Assert.NotEqual(0, Memory.Read16(0x04000202) & (ushort)Interrupt.VBlank);

        RunUntil((227 * 1232) + 16);
        Assert.Equal(0x0000, Memory.Read16(0x04000004) & 7);

        Memory.Write16(0x04000004, 0);
        Memory.Write16(0x04000202, 0xFFFF);
    }

    [Fact]
    public void FramesKeepTheWriteOnlyRegistersTheGameCannotReadBack()
    {
        VideoFrame? captured = null;
        void OnFrame(VideoFrame frame) => captured ??= frame;

        Memory.Write16(0x04000010, 0x0123);
        Video.FrameFinished += OnFrame;
        Video.Start(paced: false);
        for (int i = 0; i < CyclesPerFrame / 64; i++)
        {
            Scheduler.Cycles += 64;
            Scheduler.RunDueEvents();
        }

        Video.FrameFinished -= OnFrame;
        Assert.Equal(0, Memory.Read16(0x04000010));
        Assert.Equal(0x0123u, captured!.Lines[4] & 0xFFFF);
        Memory.Write16(0x04000010, 0);
    }
}
