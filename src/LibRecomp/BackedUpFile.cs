using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace LibRecomp;

/// <summary>
/// <c>BackedUpFile</c> reads and writes files safely. If your program dies halfway through a write, whatever was in
/// the file before is still there, so it's a good fit for saves and settings.
/// </summary>
public static class BackedUpFile
{
    private const string BackupSuffix = ".bak";
    private const string TemporarySuffix = ".temp";

    /// <summary>Writes a file, and keeps what was in it before in a <c>.bak</c> file right next to it.</summary>
    public static void Write(string path, ReadOnlySpan<byte> contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + TemporarySuffix;
        File.WriteAllBytes(temporary, contents);

        if (File.Exists(path))
        {
            File.Copy(path, path + BackupSuffix, overwrite: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Reads a file and parses it with <paramref name="parse"/>. If the file is missing or <paramref name="parse"/>
    /// throws, it tries the <c>.bak</c> file instead. Returns <c>false</c> if neither of them works.
    /// </summary>
    public static bool TryRead<T>(string path, Func<byte[], T> parse, [MaybeNullWhen(false)] out T value)
    {
        foreach (string candidate in (string[])[path, path + BackupSuffix])
        {
            try
            {
                value = parse(File.ReadAllBytes(candidate));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidDataException or JsonException)
            {
            }
        }

        value = default;
        return false;
    }
}
