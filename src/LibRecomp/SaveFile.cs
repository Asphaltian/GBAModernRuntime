using System.Text.Json;
using AGBModern;

namespace LibRecomp;

internal sealed class SaveFile
{
    private const int QuietFramesBeforeWrite = 30;

    private readonly string _path;
    private readonly string _clockPath;
    private ClockState _writtenClock;
    private int _framesUntilWrite;

    public SaveFile(string path)
    {
        _path = path;
        _clockPath = Path.ChangeExtension(path, ".rtc.json");

        if (BackedUpFile.TryRead(_path, contents => contents, out var contents))
        {
            SaveMemory.Load(contents);
        }

        _writtenClock = BackedUpFile.TryRead(_clockPath, json => JsonSerializer.Deserialize<ClockState>(json), out var clock)
            ? clock
            : ClockState.Default;
        GPIO.Clock = _writtenClock;
    }

    public void Update()
    {
        if (SaveMemory.IsModified)
        {
            SaveMemory.IsModified = false;
            _framesUntilWrite = QuietFramesBeforeWrite;
        }
        else if (_framesUntilWrite > 0 && --_framesUntilWrite == 0)
        {
            WriteSave();
        }

        if (GPIO.Clock != _writtenClock)
        {
            WriteClock();
        }
    }

    public void Flush()
    {
        if (SaveMemory.IsModified || _framesUntilWrite > 0)
        {
            SaveMemory.IsModified = false;
            _framesUntilWrite = 0;
            WriteSave();
        }

        if (GPIO.Clock != _writtenClock)
        {
            WriteClock();
        }
    }

    private void WriteSave()
    {
        if (SaveMemory.Data.Length > 0)
        {
            BackedUpFile.Write(_path, SaveMemory.Data);
        }
    }

    private void WriteClock()
    {
        _writtenClock = GPIO.Clock;
        BackedUpFile.Write(_clockPath, JsonSerializer.SerializeToUtf8Bytes(_writtenClock));
    }
}
