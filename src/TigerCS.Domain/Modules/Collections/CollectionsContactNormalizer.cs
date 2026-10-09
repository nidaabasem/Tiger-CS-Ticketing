using System.Net.Mail;

namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// Phone and e-mail normalisation used by campaigns. It is the single definition: the in-memory evaluation calls it directly and the SQL campaign
/// engine reads its results from <c>dbo.CollectionsContactNorm</c>, which the application fills with this code. Bump <see cref="Version"/> whenever
/// either rule changes so stored normalisations are recomputed.
/// </summary>
public static class CollectionsContactNormalizer
{
    public const byte Version = 2;   // 2: phone rule replaced by Review.PhoneNormalizer (merge of the review / dispatch workflow)

    /// <summary>The callable E.164 number of the source value, or an empty string when it cannot be confirmed (the review workflow's rule).</summary>
    public static string NormalizePhone(string value) => Review.PhoneNormalizer.Normalize(value).E164;

    /// <summary>The address itself when it is a plain, valid address; an empty string otherwise.</summary>
    public static string NormalizeEmail(string value) =>
        MailAddress.TryCreate(value.Trim(), out var address) && address.Address == value.Trim() ? address.Address : "";
}
