using System.Text.RegularExpressions;

namespace TigerCS.Domain.Modules.Collections.Review;

public enum PhoneRejection
{
    None = 0,
    Empty,
    NotNumeric,
    MultipleNumbers,
    InvalidLength,
    InvalidUaeNumber
}

/// <summary>The outcome of normalizing one raw source phone value to international (E.164) format.</summary>
public sealed record PhoneResult(string E164, PhoneRejection Rejection, bool Repaired)
{
    public bool IsValid => Rejection == PhoneRejection.None && E164.Length > 0;
    public static PhoneResult Invalid(PhoneRejection reason) => new("", reason, false);
}

/// <summary>
/// Converts the free-text mobile values stored in PACT to E.164. Rules, in order:
/// separators (space - . ( ) / \ ) are removed; "00" becomes "+"; a bare UAE national form is repaired
/// (0501234567, 501234567 with the leading zero lost to a spreadsheet, 971501234567, 9710501234567 with a
/// stray trunk zero). Two different numbers in one field are never guessed between. Nothing is invented: a value
/// that cannot be confirmed is rejected with a reason the reviewer can read.
/// </summary>
public static class PhoneNormalizer
{
    private static readonly Regex Separators = new(@"[\s\-\.\(\)\\]", RegexOptions.Compiled);
    private static readonly Regex Splitter = new(@"[/,;|]", RegexOptions.Compiled);
    // UAE mobile prefixes 50, 52, 54, 55, 56, 58; landline area codes 2,3,4,6,7,9.
    private static readonly Regex UaeMobile = new(@"^5[024568]\d{7}$", RegexOptions.Compiled);
    private static readonly Regex UaeLandline = new(@"^[234679]\d{7}$", RegexOptions.Compiled);

    public static PhoneResult Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return PhoneResult.Invalid(PhoneRejection.Empty);

        var parts = Splitter.Split(raw).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        var results = parts.Select(NormalizeOne).ToList();
        var valid = results.Where(r => r.IsValid).Select(r => r.E164).Distinct(StringComparer.Ordinal).ToList();
        if (valid.Count == 1) return results.First(r => r.IsValid);
        if (valid.Count > 1) return PhoneResult.Invalid(PhoneRejection.MultipleNumbers);
        return results.Count > 0 ? results[0] : PhoneResult.Invalid(PhoneRejection.Empty);
    }

    private static PhoneResult NormalizeOne(string part)
    {
        var compact = Separators.Replace(part, "");
        if (compact.Length == 0) return PhoneResult.Invalid(PhoneRejection.Empty);
        var hadPlus = compact.StartsWith('+');
        var digits = hadPlus ? compact[1..] : compact;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return PhoneResult.Invalid(PhoneRejection.NotNumeric);

        var repaired = false;
        if (!hadPlus && digits.StartsWith("00", StringComparison.Ordinal)) { digits = digits[2..]; hadPlus = true; }

        string national;
        if (hadPlus)
        {
            if (digits.StartsWith("9710", StringComparison.Ordinal)) { digits = "971" + digits[4..]; repaired = true; }
            return Finish(digits, repaired);
        }

        // No explicit international prefix: only UAE forms are repaired, because the source is UAE property.
        if (digits.StartsWith("971", StringComparison.Ordinal))
        {
            if (digits.StartsWith("9710", StringComparison.Ordinal)) digits = "971" + digits[4..];
            return Finish(digits, true);
        }
        if (digits.StartsWith('0')) { national = digits[1..]; repaired = true; }
        else { national = digits; repaired = true; }
        if (UaeMobile.IsMatch(national) || UaeLandline.IsMatch(national)) return Finish("971" + national, repaired);
        return PhoneResult.Invalid(national.Length is < 8 or > 9 ? PhoneRejection.InvalidLength : PhoneRejection.InvalidUaeNumber);
    }

    private static PhoneResult Finish(string digits, bool repaired)
    {
        if (digits.Length is < 8 or > 15 || digits[0] == '0') return PhoneResult.Invalid(PhoneRejection.InvalidLength);
        if (digits.StartsWith("971", StringComparison.Ordinal))
        {
            var national = digits[3..];
            if (!UaeMobile.IsMatch(national) && !UaeLandline.IsMatch(national))
                return PhoneResult.Invalid(national.Length is < 8 or > 9 ? PhoneRejection.InvalidLength : PhoneRejection.InvalidUaeNumber);
        }
        return new PhoneResult("+" + digits, PhoneRejection.None, repaired);
    }

    public static string Explain(PhoneRejection rejection) => rejection switch
    {
        PhoneRejection.Empty => "No phone number on the account",
        PhoneRejection.NotNumeric => "Phone number contains letters or symbols",
        PhoneRejection.MultipleNumbers => "Several different phone numbers in one field",
        PhoneRejection.InvalidLength => "Phone number has the wrong number of digits",
        PhoneRejection.InvalidUaeNumber => "Not a valid UAE mobile or landline number",
        _ => ""
    };
}
