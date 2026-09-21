using System.Security.Cryptography;
using System.Text;
using AGBModern;

namespace LibRecomp.Tests;

public sealed class RuntimeTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runtime").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static byte[] ROM(string title, byte revision = 0)
    {
        byte[] rom = new byte[0x200];
        rom[0x100] = revision;
        Encoding.ASCII.GetBytes(title).CopyTo(rom, 0xA0);
        rom[0xB2] = 0x96;
        return rom;
    }

    private string WriteFile(string name, byte[] contents)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("0.10.20-beta", 0, 10, 20, "-beta")]
    public void VersionsParseWithTheirSuffix(string text, int major, int minor, int patch, string suffix)
    {
        Assert.Equal(new SemanticVersion(major, minor, patch, suffix), SemanticVersion.Parse(text));
        Assert.Equal(text, SemanticVersion.Parse(text).ToString());
    }

    [Fact]
    public void CodeCopiedToRAMRunsTheFunctionItWasCopiedFromWhenTwoLookAlike()
    {
        const uint First = 0x08000300;
        const uint Second = 0x08000340;
        const uint Copy = 0x03000300;
        byte[] body = [0x70, 0x47, 0x00, 0x00];
        body.CopyTo(Memory.ROM, (int)(First & 0x1FFFFFF));
        body.CopyTo(Memory.ROM, (int)(Second & 0x1FFFFFF));
        Memory.ROM[(First & 0x1FFFFFF) + 4] = 1;
        Memory.ROM[(Second & 0x1FFFFFF) + 4] = 2;
        Memory.ROM.AsSpan((int)(Second & 0x1FFFFFF), 8).CopyTo(Memory.IWRAM.AsSpan((int)(Copy & 0x7FFF)));

        uint? ran = null;
        Recomp.RegisterRAMFunctions([(First, 4, _ => ran = First), (Second, 4, _ => ran = Second)]);
        Recomp.LookupFunc(Copy)(new RecompContext());

        Assert.Equal(Second, ran);
    }

    [Theory]
    [InlineData(0x03007000u, true)]
    [InlineData(0x08FFF000u, false)]
    public void MissingFunctionsInRAMPointToRAMFuncs(uint address, bool mentionsRAMFuncs)
    {
        Recomp.RegisterRAMFunctions([]);

        var error = Assert.Throws<InvalidOperationException>(() => Recomp.LookupFunc(address));

        Assert.Equal(mentionsRAMFuncs, error.Message.Contains("input.ram_funcs"));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    [InlineData("a.2.3")]
    [InlineData(" 1.2.3")]
    [InlineData("-1.2.3")]
    public void AVersionNeedsThreeNumbers(string text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void VersionsCompareByNumberAlone()
    {
        Assert.True(SemanticVersion.Parse("1.2.0") < SemanticVersion.Parse("1.10.0"));
        Assert.Equal(0, SemanticVersion.Parse("1.2.0-beta").CompareTo(SemanticVersion.Parse("1.2.0")));
    }

    [Fact]
    public void AFileKeepsWhatItHeldBeforeAsItsBackup()
    {
        string path = Path.Combine(_folder, "data.bin");
        BackedUpFile.Write(path, [1]);
        BackedUpFile.Write(path, [2]);

        Assert.Equal([1], File.ReadAllBytes(path + ".bak"));

        File.WriteAllBytes(path, [0xFF]);
        Assert.True(BackedUpFile.TryRead(path, bytes => bytes[0] == 0xFF ? throw new InvalidDataException() : bytes, out var contents));
        Assert.Equal([1], contents);
    }

    [Fact]
    public void TheSaveIsWrittenOnceTheGameStopsChangingItAndTheClockWithIt()
    {
        string path = Path.Combine(_folder, "saves", "game.sav");
        SaveMemory.Initialize(SaveType.SRAM);
        var save = new SaveFile(path);

        Memory.Write8(0x0E000010, 0x42);
        GPIO.Clock = GPIO.Clock with { Control = 0 };
        for (int frame = 0; frame < 31; frame++)
        {
            save.Update();
        }

        Assert.Equal(0x42, File.ReadAllBytes(path)[0x10]);
        Assert.True(File.Exists(Path.Combine(_folder, "saves", "game.rtc.json")));

        SaveMemory.Initialize(SaveType.SRAM);
        GPIO.Clock = ClockState.Default;
        _ = new SaveFile(path);
        Assert.Equal(0x42, SaveMemory.Data[0x10]);
        Assert.Equal(0, GPIO.Clock.Control);
        GPIO.Clock = ClockState.Default;
    }

    [Fact]
    public void ROMsAreCheckedAndTheRightOneIsStored()
    {
        byte[] good = ROM("TESTGAME");
        var game = new GameEntry("romtest", "romtest", "TESTGAME", Convert.ToHexString(SHA1.HashData(good)), SaveType.None, GPIODevices.None, _ => { });
        Recomp.RegisterConfigPath(_folder);
        Recomp.RegisterGame(game);

        Assert.False(Recomp.IsROMValid("romtest"));
        Assert.Equal(ROMValidationError.FailedToOpen, Recomp.SelectROM(Path.Combine(_folder, "missing.gba"), "romtest"));
        Assert.Equal(ROMValidationError.NotAROM, Recomp.SelectROM(WriteFile("text.gba", new byte[0x200]), "romtest"));
        Assert.Equal(ROMValidationError.IncorrectROM, Recomp.SelectROM(WriteFile("other.gba", ROM("OTHERGAME")), "romtest"));
        Assert.Equal(ROMValidationError.IncorrectVersion, Recomp.SelectROM(WriteFile("revision.gba", ROM("TESTGAME", revision: 1)), "romtest"));
        Assert.False(Recomp.IsROMValid("romtest"));

        Assert.Equal(ROMValidationError.Good, Recomp.SelectROM(WriteFile("good.gba", good), "romtest"));
        Assert.True(Recomp.IsROMValid("romtest"));
    }
}
