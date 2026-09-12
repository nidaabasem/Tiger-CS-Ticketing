using System.Net.Mail;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// Email-address hygiene shared by recipient resolution and the send
/// boundary. Deliberately stricter than <see cref="MailAddress"/> alone,
/// which happily accepts display-name forms and hostnames without a dot —
/// the contact channel this is applied to is an untyped "phone or email"
/// string, so anything that is not unambiguously one bare address is
/// treated as not an address.
/// </summary>
public static class CustomerEmailAddress
{
    public const int MaxLength = 320;

    /// <summary>Trimmed value, or <c>null</c> for a null/blank input. Never validates.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>True only for a single bare <c>local@domain.tld</c> address.</summary>
    public static bool IsValid(string? value)
    {
        var address = Normalize(value);
        if (address is null || address.Length > MaxLength)
        {
            return false;
        }

        if (address.Contains(' ', StringComparison.Ordinal) || address.Count(c => c == '@') != 1)
        {
            return false;
        }

        if (!MailAddress.TryCreate(address, out var parsed) || parsed is null)
        {
            return false;
        }

        return string.Equals(parsed.Address, address, StringComparison.OrdinalIgnoreCase)
            && parsed.Host.Contains('.', StringComparison.Ordinal)
            && !parsed.Host.StartsWith('.')
            && !parsed.Host.EndsWith('.');
    }

    /// <summary>
    /// <c>a***@example.com</c> — enough to correlate a log line with a
    /// customer complaint, not enough to reconstruct the address
    /// (Security-Architecture.md §11: contact details masked or omitted from
    /// log output).
    /// </summary>
    public static string Mask(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return "(none)";
        }

        var at = address.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
        {
            return "***";
        }

        return $"{address[..1]}***{address[at..]}";
    }
}
