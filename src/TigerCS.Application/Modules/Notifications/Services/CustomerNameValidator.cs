namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// Decides whether a stored customer name is fit to appear in a customer-facing greeting. Source systems write placeholders
/// such as <c>NOT_REGISTERED</c>, <c>UNKNOWN</c> or <c>N/A</c> into name fields; those must never reach a customer as
/// "Dear NOT_REGISTERED,".
/// </summary>
public static class CustomerNameValidator
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOTREGISTERED", "UNREGISTERED", "UNKNOWN", "UNKNOWNCUSTOMER", "NA", "NONE", "NULL", "NIL", "NAN", "EMPTY",
        "NOTAVAILABLE", "NOTPROVIDED", "NOTSPECIFIED", "NONAME", "ANONYMOUS", "TBD", "TEST", "CUSTOMER", "GUEST", "UNDEFINED"
    };

    /// <summary>The trimmed name when it is a real name; otherwise <c>null</c>.</summary>
    public static string? Sanitize(string? name)
    {
        var trimmed = name?.Trim();
        return IsValid(trimmed) ? trimmed : null;
    }

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.Any(char.IsLetter))
        {
            return false;
        }

        // "NOT_REGISTERED", "not registered", "N/A", "n.a." all collapse to the same key.
        var key = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return !Placeholders.Contains(key);
    }
}
