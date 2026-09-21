using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace LibRecomp;

/// <summary>Reads and writes files safely. If a write dies halfway through, whatever was there before is still around.</summary>
public static class BackedUpFile
{
    private const string BackupSuffix = ".bak";
    private const string TemporarySuffix = ".temp";

    /// <summary>Writes a file, and keeps what it held before in a <c>.bak</c> file next to it.</summary>
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
    /// Reads and parses a file, or its <c>.bak</c> file if the file is missing or
    /// <paramref name="parse"/> throws. Returns false if neither works.
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
