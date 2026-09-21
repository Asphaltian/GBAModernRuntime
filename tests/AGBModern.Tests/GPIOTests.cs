namespace AGBModern.Tests;

public sealed class GPIOTests : IDisposable
{
    private const uint Data = 0x080000C4;
    private const uint Direction = 0x080000C6;
    private const uint Control = 0x080000C8;

    private const int Clock = 1 << 0;
    private const int Serial = 1 << 1;
    private const int Select = 1 << 2;

    private readonly FixedTime _host = new(new DateTime(2026, 9, 23, 14, 5, 9));

    public GPIOTests()
    {
        byte[] rom = new byte[0x200];
        rom[0xC4] = 0xAB;
        Memory.LoadROM(rom);
        GPIO.Host = _host;
        GPIO.Clock = ClockState.Default;
    }

    public void Dispose() => GPIO.Host = TimeProvider.System;

    private static void Begin()
    {
        Memory.Write16(Direction, Clock | Serial | Select);
        Memory.Write16(Data, Clock);
        Memory.Write16(Data, Clock | Select);
    }

    private static void End() => Memory.Write16(Data, Clock);

    private static void WriteByte(int value)
    {
        Memory.Write16(Direction, Clock | Serial | Select);
        for (int i = 0; i < 8; i++)
        {
            int bit = ((value >> i) & 1) << 1;
            Memory.Write16(Data, (ushort)(Select | bit));
            Memory.Write16(Data, (ushort)(Select | Clock | bit));
        }
    }

    private static byte ReadByte()
    {
        Memory.Write16(Direction, Clock | Select);
        int value = 0;
        for (int i = 0; i < 8; i++)
        {
            Memory.Write16(Data, Select);
            Memory.Write16(Data, Select | Clock);
            value |= ((Memory.Read16(Data) & Serial) >> 1) << i;
        }

        return (byte)value;
    }

    private static byte[] Read(int command, int count)
    {
        Begin();
        WriteByte(0x86 | (command << 4));
        byte[] bytes = [.. Enumerable.Range(0, count).Select(_ => ReadByte())];
        End();
        return bytes;
    }

    private static void Write(int command, params byte[] bytes)
    {
        Begin();
        WriteByte(0x06 | (command << 4));
        foreach (byte value in bytes)
        {
            WriteByte(value);
        }

        End();
    }

    [Fact]
    public void RegistersReadAsROMUntilTheyAreMadeReadable()
    {
        GPIO.Connect(GPIODevices.RTC);
        Memory.Write16(Direction, 0x7);

        Assert.Equal(0xAB, Memory.Read8(Data));

        Memory.Write16(Control, 1);
        Assert.Equal(0x7, Memory.Read16(Direction));

        Memory.Write16(Control, 0);
        Assert.Equal(0xAB, Memory.Read8(Data));
    }

    [Fact]
    public void AFreshClockReportsTheLostTimeOnce()
    {
        GPIO.Connect(GPIODevices.RTC);
        Memory.Write16(Control, 1);

        Assert.Equal([0x82], Read(4, 1));
        Assert.Equal([0x02], Read(4, 1));
    }

    [Fact]
    public void TheClockReadsTheHostTimeInBCDWithThePMFlag()
    {
        GPIO.Clock = ClockState.Default with { Control = 0x40 };
        GPIO.Connect(GPIODevices.RTC);
        Memory.Write16(Control, 1);

        Assert.Equal([0x26, 0x09, 0x23, 3, 0x94, 0x05, 0x09], Read(2, 7));
        Assert.Equal([0x94, 0x05, 0x09], Read(6, 3));
        Assert.Equal([0x40], Read(4, 1));
    }

    [Fact]
    public void WritingTheDateAndTimeSetsTheClock()
    {
        GPIO.Connect(GPIODevices.RTC);
        Memory.Write16(Control, 1);

        Write(2, 0x01, 0x02, 0x03, 5, 0x04, 0x05, 0x06);
        _host.Now += TimeSpan.FromDays(1) + TimeSpan.FromSeconds(10);

        Assert.Equal([0x01, 0x02, 0x04, 6, 0x04, 0x05, 0x16], Read(2, 7));
        Assert.NotEqual(ClockState.Default, GPIO.Clock);
    }

    [Fact]
    public void AForceResetClearsTheClock()
    {
        GPIO.Connect(GPIODevices.RTC);
        Memory.Write16(Control, 1);

        Write(0);

        Assert.Equal([0x00], Read(4, 1));
        Assert.Equal([0x00, 0x01, 0x01, 0, 0x00, 0x00, 0x00], Read(2, 7));
    }

    [Fact]
    public void AnyAccessToForceIRQRequestsAGamePakInterrupt()
    {
        GPIO.Connect(GPIODevices.RTC);
        Memory.Write16(Control, 1);

        Memory.Write16(0x04000202, 0xFFFF);
        Write(3);
        Assert.Equal((ushort)Interrupt.GamePak, Memory.Read16(0x04000202) & (ushort)Interrupt.GamePak);

        Memory.Write16(0x04000202, 0xFFFF);
        Read(3, 0);
        Assert.Equal((ushort)Interrupt.GamePak, Memory.Read16(0x04000202) & (ushort)Interrupt.GamePak);
        Memory.Write16(0x04000202, 0xFFFF);
    }

    [Fact]
    public void UnusedRegistersReadAsAllOnes()
    {
        GPIO.Connect(GPIODevices.RTC);
        Memory.Write16(Control, 1);

        Assert.Equal([0xFF], Read(7, 1));
    }

    [Fact]
    public void RumbleFollowsPinThreeAndStopsWhenItIsAnInput()
    {
        GPIO.Connect(GPIODevices.Rumble);
        var states = new List<bool>();
        GPIO.RumbleChanged += states.Add;

        Memory.Write16(Direction, 8);
        Memory.Write16(Data, 8);
        Memory.Write16(Direction, 0);

        GPIO.RumbleChanged -= states.Add;
        Assert.Equal([true, false], states);
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }
}
