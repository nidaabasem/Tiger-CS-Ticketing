namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// Tower number of a PACT unit code. <c>TP124-1001</c> and <c>124-1001</c> are tower <c>124</c>; <c>TP136-C-402</c> and
/// <c>136-C-402</c> are tower <c>136</c>. Mirrors SQL <c>dbo.fn_CollectionsTowerNumber</c> exactly (trim, drop ONE optional
/// leading "TP", take the text before the first hyphen, trim; blank => null). ProjectCode is never used: it is blank in PACT output.
/// </summary>
public static class CollectionsTowerNumber
{
    public static string? Parse(string? unitCode)
    {
        var value = unitCode?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith("TP", StringComparison.OrdinalIgnoreCase)) value = value[2..].TrimStart();
        var hyphen = value.IndexOf('-');
        if (hyphen >= 0) value = value[..hyphen];
        value = value.Trim();
        return value.Length == 0 ? null : value.Length > 20 ? value[..20] : value;
    }

    /// <summary>Tower numbers compare as trimmed, case-insensitive text (the seed table may store them as numbers or text).</summary>
    public static bool Matches(string? unitDerived, string? towerTableNumber) =>
        unitDerived is not null && towerTableNumber is not null
        && string.Equals(unitDerived.Trim(), towerTableNumber.Trim(), StringComparison.OrdinalIgnoreCase);
}
