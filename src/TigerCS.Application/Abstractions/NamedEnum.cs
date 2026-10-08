namespace TigerCS.Application.Abstractions;

/// <summary>
/// Parses an enum from its <b>name</b> only. <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> also accepts
/// numeric strings (<c>"3"</c>, <c>"-1"</c>, <c>"99"</c>) and comma lists, so a public API that "takes a word" would silently
/// accept an internal enum number - and a number that happens to be defined means something else after the enum is reordered.
/// The wire contract of every Genesys-facing enum is the word, never the number.
/// </summary>
public static class NamedEnum
{
    /// <summary>True only when <paramref name="value"/> (trimmed, case-insensitive) equals one of the enum's member names.</summary>
    public static bool TryParse<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                result = Enum.Parse<TEnum>(name);
                return true;
            }
        }

        return false;
    }
}
