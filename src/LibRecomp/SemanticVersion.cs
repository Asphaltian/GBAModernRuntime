using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibRecomp;

/// <summary>
/// A version like <c>1.2.3</c>. Anything after the patch number, like the <c>-beta</c> in
/// <c>1.2.3-beta</c>, is kept, but it's ignored when comparing versions.
/// </summary>
[JsonConverter(typeof(JsonConverter))]
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string Suffix = "") : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string text)
    {
        return TryParse(text, out var version) ? version : throw new FormatException($"\"{text}\" is not a major.minor.patch version.");
    }

    public static bool TryParse(string text, out SemanticVersion version)
    {
        version = default;
        string[] parts = text.Split('.', 3);
        if (parts.Length != 3 || !TryParseNumber(parts[0], out int major) || !TryParseNumber(parts[1], out int minor))
        {
            return false;
        }

        int digits = parts[2].TakeWhile(char.IsAsciiDigit).Count();
        if (!TryParseNumber(parts[2][..digits], out int patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, parts[2][digits..]);
        return true;
    }

    private static bool TryParseNumber(string text, out int value) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    public int CompareTo(SemanticVersion other)
    {
        return (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}{Suffix}";

    private sealed class JsonConverter : JsonConverter<SemanticVersion>
    {
        public override SemanticVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string text = reader.GetString() ?? "";
            return TryParse(text, out var version) ? version : throw new JsonException($"\"{text}\" is not a major.minor.patch version.");
        }

        public override void Write(Utf8JsonWriter writer, SemanticVersion value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}
