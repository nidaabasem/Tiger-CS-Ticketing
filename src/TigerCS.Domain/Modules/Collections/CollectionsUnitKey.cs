namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// The key that links a unit across PACT and CRM: <c>TP{tower}-{unit}</c>, upper case, trimmed (<c>TP140-101</c>, <c>TP136-C-402</c>). PACT already issues it as the
/// unit code; CRM's project code plus unit number form the same text (the legacy EDSM exclusion builds it as <c>ProjectCode + '-' + UnitNumber</c>).
/// Mirrors SQL <c>dbo.fn_CollectionsUnitKey</c>. Names, phones and customer names are never part of it: nothing is matched by text similarity.
/// </summary>
public static class CollectionsUnitKey
{
    /// <summary>Normalises a PACT unit code (or any <c>TP140-101</c> / <c>140-101</c> text). Blank gives null.</summary>
    public static string? Normalize(string? unitCode)
    {
        var value = unitCode?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith("TP", StringComparison.OrdinalIgnoreCase)) value = value[2..].TrimStart();
        return value.Length == 0 ? null : "TP" + value.ToUpperInvariant();
    }

    /// <summary>The key of a CRM unit: its project code (<c>TP140</c> or <c>140</c>) and its unit number. Either part blank gives null (the sale cannot be linked).</summary>
    public static string? FromCrm(string? projectCode, string? unitNumber)
    {
        var project = projectCode?.Trim();
        var unit = unitNumber?.Trim();
        if (string.IsNullOrEmpty(project) || string.IsNullOrEmpty(unit)) return null;
        return Normalize(project + "-" + unit);
    }

    /// <summary>A cancelled apartment (a '*' in its code, e.g. <c>513*</c>), a blank code or <c>0</c> is never a unit to link or list.</summary>
    public static bool IsListable(string? unitCode)
    {
        var value = unitCode?.Trim();
        return !string.IsNullOrEmpty(value) && value != "0" && !value.Contains('*');
    }
}
