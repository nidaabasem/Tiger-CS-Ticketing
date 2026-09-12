namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The one canonical form of a customer phone number, and the only place
/// TigerCS is allowed to decide that two differently written numbers belong
/// to the same person.
///
/// <para>
/// A number is captured exactly as the agent typed it — "+971501234567",
/// "971501234567", "+971 50 123 4567", "+971-50-123-4567" are all the same
/// customer, and every one of them is persisted verbatim
/// (<c>IntakeRecord.PhoneNumber</c> is never rewritten; see
/// <c>IIntakeRecordRepository</c>). Identity, grouping and matching
/// therefore cannot compare the stored strings: they compare
/// <see cref="Normalize"/>'s output.
/// </para>
///
/// <para>
/// This is a read-side rule only. Nothing here reformats what is stored or
/// what an integration is sent — <see cref="WithoutPlus"/> is the separate,
/// narrower rule the PACT request path has always applied to its own URLs.
/// </para>
/// </summary>
public static class CustomerPhoneNumber
{
    /// <summary>
    /// The separator characters a phone number is written with, which
    /// <see cref="Normalize"/> drops: '+', space, hyphen and parentheses.
    /// A database query cannot run <see cref="Normalize"/>, so the Customers
    /// directory drops exactly these in SQL with chained <c>REPLACE()</c>;
    /// for a value made of digits and separators — which is what a phone
    /// number is — the two agree, and <see cref="Normalize"/> stays the
    /// authority: it is applied again, in memory, to every directory key.
    /// </summary>
    public const string SeparatorCharacters = "+ -()";

    /// <summary>
    /// The canonical form: the number's digits, in order, and nothing else.
    /// Surrounding whitespace, '+', spaces, hyphens and parentheses all
    /// disappear, so "+971 50 123 4567", "971-50-123-4567" and
    /// "+971501234567" all normalize to "971501234567". A number with no
    /// digits at all (null, blank, "+") normalizes to the empty string,
    /// which is not an identity.
    /// </summary>
    public static string Normalize(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return string.Empty;
        }

        var digits = new char[phoneNumber.Length];
        var length = 0;
        foreach (var character in phoneNumber)
        {
            if (char.IsAsciiDigit(character))
            {
                digits[length++] = character;
            }
        }

        return length == 0 ? string.Empty : new string(digits, 0, length);
    }

    /// <summary>
    /// True when a search term is shaped like a phone number — it has at
    /// least one digit and contains nothing but digits and
    /// <see cref="SeparatorCharacters"/>.
    ///
    /// <para>
    /// This is the gate for matching a free-text search term canonically.
    /// <see cref="Normalize"/> alone is not that test: it strips every
    /// non-digit, so it happily reduces the unit code "M-401" to "401" and
    /// would then match it against any caller whose number merely contains
    /// those digits. A term is only matched as a phone number when it
    /// actually looks like one; everything else is matched as the text it is.
    /// </para>
    /// </summary>
    public static bool LooksLikeNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var hasDigit = false;
        foreach (var character in value.Trim())
        {
            if (char.IsAsciiDigit(character))
            {
                hasDigit = true;
            }
            else if (!SeparatorCharacters.Contains(character))
            {
                return false;
            }
        }

        return hasDigit;
    }

    /// <summary>True when both numbers name the same customer once written the same way; false when either has no digits.</summary>
    public static bool AreSameNumber(string? left, string? right)
    {
        var canonicalLeft = Normalize(left);
        return canonicalLeft.Length > 0 && string.Equals(canonicalLeft, Normalize(right), StringComparison.Ordinal);
    }

    /// <summary>
    /// PACT's request form — trimmed, with every '+' removed, and otherwise
    /// untouched. Deliberately <b>not</b> <see cref="Normalize"/>: PACT
    /// matches on the number as it stores it (without the '+' prefix), and
    /// the rest of the string is passed through as typed. Applied to the
    /// PACT request path only; CRM and persistence keep the number exactly
    /// as entered.
    /// </summary>
    public static string WithoutPlus(string? phoneNumber) =>
        string.IsNullOrWhiteSpace(phoneNumber) ? string.Empty : phoneNumber.Trim().Replace("+", string.Empty);
}
