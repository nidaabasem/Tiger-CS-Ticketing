using System.Net.Mail;

namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// Phone and e-mail normalisation used by campaigns. It is the single definition: the in-memory evaluation calls it directly and the SQL campaign
/// engine reads its results from <c>dbo.CollectionsContactNorm</c>, which the application fills with this code. Bump <see cref="Version"/> whenever
/// either rule changes so stored normalisations are recomputed.
/// </summary>
public static class CollectionsContactNormalizer
{
    public const byte Version = 1;

    /// <summary>E.164 for UAE-style numbers; an empty string means "not a valid phone".</summary>
    public static string NormalizePhone(string value)
    {
        var compact = new string(value.Where(c => c is not (' ' or '-' or '(' or ')')).ToArray());
        if (compact.StartsWith("00", StringComparison.Ordinal)) compact = "+" + compact[2..];
        if (compact.Length == 10 && compact.StartsWith("05", StringComparison.Ordinal)) compact = "+971" + compact[1..];
        if (compact.Length == 12 && compact.StartsWith("971", StringComparison.Ordinal)) compact = "+" + compact;
        return compact.StartsWith('+') && compact.Length is >= 9 and <= 16 && compact[1] != '0'
            && compact[1..].All(char.IsAsciiDigit) ? compact : "";
    }

    /// <summary>The address itself when it is a plain, valid address; an empty string otherwise.</summary>
    public static string NormalizeEmail(string value) =>
        MailAddress.TryCreate(value.Trim(), out var address) && address.Address == value.Trim() ? address.Address : "";
}
