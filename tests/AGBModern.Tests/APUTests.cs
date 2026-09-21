namespace AGBModern.Tests;

public class APUTests
{
    private const uint SOUND2CNT_L = 0x04000068;
    private const uint SOUND2CNT_H = 0x0400006C;
    private const uint SOUNDCNT_L = 0x04000080;
    private const uint SOUNDCNT_H = 0x04000082;
    private const uint SOUNDCNT_X = 0x04000084;

    private static readonly List<short> Samples = [];

    static APUTests()
    {
        APU.SamplesReady += (samples, _) => Samples.AddRange(samples);
        APU.Start();
    }

    private static List<short> Run(int sampleCount)
    {
        Samples.Clear();
        while (Samples.Count < sampleCount * 2)
        {
            Scheduler.Cycles += 32768;
            Scheduler.RunDueEvents();
        }

        return Samples;
    }

    [Fact]
    public void ASquareChannelPlaysAtItsFrequency()
    {
        Memory.Write16(SOUNDCNT_X, 0x0080);
        Memory.Write16(SOUNDCNT_L, 0x2277);
        Memory.Write16(SOUNDCNT_H, 0x0002);

        Memory.Write16(SOUND2CNT_L, 0xF080);
        Memory.Write16(SOUND2CNT_H, 0x8600);

        var samples = Run(APU.SampleRate);
        int risingEdges = 0;
        for (int i = 2; i < APU.SampleRate * 2; i += 2)
        {
            if (samples[i - 2] < 0 && samples[i] > 0)
            {
                risingEdges++;
            }
        }

        Assert.InRange(risingEdges, 255, 257);
        Assert.Equal(1, (Memory.Read16(SOUNDCNT_X) >> 1) & 1);

        Memory.Write16(SOUNDCNT_X, 0);
    }

    [Fact]
    public void ASquareChannelWithALengthStopsWhenItRunsOut()
    {
        Memory.Write16(SOUNDCNT_X, 0x0080);
        Memory.Write16(SOUND2CNT_L, 0xF03F);
        Memory.Write16(SOUND2CNT_H, 0xC600);
        Assert.Equal(1, (Memory.Read16(SOUNDCNT_X) >> 1) & 1);

        Run(APU.SampleRate / 128);

        Assert.Equal(0, (Memory.Read16(SOUNDCNT_X) >> 1) & 1);
        Memory.Write16(SOUNDCNT_X, 0);
    }

    [Fact]
    public void WaveRAMShiftsAsItPlays()
    {
        var wave = new PSG.WaveChannel();
        wave.WriteControl(0x80);
        for (int i = 0; i < 16; i++)
        {
            wave.WriteWaveRAM(i, (byte)(0x12 + (i * 0x22)));
        }

        wave.WriteControl(0xC0);
        wave.WriteVolume(0x20);
        wave.WriteFrequencyLow(0x00);
        wave.WriteFrequencyHigh(0x87);
        Assert.Equal((1 * 2) - 15, wave.Output);

        wave.Advance(2048);
        Assert.Equal((2 * 2) - 15, wave.Output);

        wave.WriteControl(0x80);
        Assert.Equal(0x23, wave.ReadWaveRAM(0));
        Assert.Equal(0x01, wave.ReadWaveRAM(15));
    }

    [Fact]
    public void ByteWritesLeaveTheOtherHalfOfARegisterAlone()
    {
        Memory.Write16(SOUNDCNT_X, 0x0080);
        Memory.Write16(SOUNDCNT_L, 0x2277);
        Memory.Write16(SOUNDCNT_H, 0x0002);

        Memory.Write8(SOUND2CNT_L + 1, 0xF0);
        Memory.Write8(SOUND2CNT_L, 0x80);
        Memory.Write8(SOUND2CNT_H, 0x00);
        Memory.Write8(SOUND2CNT_H + 1, 0x86);

        var samples = Run(1024);
        Assert.Contains(samples, s => s > 0);
        Assert.Contains(samples, s => s < 0);

        Memory.Write16(SOUNDCNT_X, 0);
    }

    [Fact]
    public void DirectSoundSamplesChangeWhenTheTimerOverflowedNotWhenThatWasNoticed()
    {
        const uint FIFO_A = 0x040000A0;
        const uint TM0CNT_L = 0x100;

        Memory.Write16(SOUNDCNT_X, 0x0080);
        Memory.Write16(SOUNDCNT_H, 0x0B04);
        Memory.Write32(FIFO_A, 0xC040C040);
        Memory.Write32(FIFO_A, 0xC040C040);
        Run(64);

        IO.Write16(TM0CNT_L, 0x10000 - 1024);
        IO.Write16(TM0CNT_L + 2, 0x0080);

        Samples.Clear();
        Scheduler.Cycles += 1024 * 8;
        Scheduler.RunDueEvents();
        Run(32);

        IO.Write16(TM0CNT_L + 2, 0);
        Memory.Write16(SOUNDCNT_X, 0);

        var left = Samples.Where((_, i) => i % 2 == 0).ToList();
        int first = left.FindIndex(s => s != 0);
        Assert.InRange(first, 1, 4);
        Assert.Equal(
            [16384, 16384, -16384, -16384, 16384, 16384, -16384, -16384],
            left.Skip(first).Take(8).Select(s => (int)s));
    }

    [Fact]
    public void SOUNDBIASSelectsTheSampleRateAndBitDepth()
    {
        const uint SOUNDBIAS = 0x04000088;

        Assert.Equal(32768, APU.SampleRate);

        Memory.Write16(SOUNDBIAS, 0x4200);
        Assert.Equal(65536, APU.SampleRate);

        long before = Scheduler.Cycles;
        Run(1000);
        Assert.InRange(Scheduler.Cycles - before, 1000 * 256, (1000 * 256) + 32768);

        Memory.Write16(SOUNDBIAS, 0x0200);
    }

    [Fact]
    public void TurningTheMasterSwitchOffSilencesAndClearsThePSG()
    {
        Memory.Write16(SOUNDCNT_X, 0x0080);
        Memory.Write16(SOUNDCNT_L, 0x2277);
        Memory.Write16(SOUNDCNT_X, 0);

        Assert.Equal(0, Memory.Read16(SOUNDCNT_L));
        Assert.All(Run(256), s => Assert.Equal(0, s));
    }
}
