using System.Globalization;

namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// The one ordering of towers and of the units inside a tower (Receivables, Campaigns, the tower picker): the newest tower first, i.e. the tower NUMBER descending
/// (<c>TP140</c>, <c>TP132</c>, <c>TP131</c> ... <c>TP119</c>). The number is the actual integer of the tower, never the text (as text <c>99</c> sorts after <c>140</c>) and never
/// its name. A tower number that is not a plain integer sorts after all numeric towers, in a fixed text order, so the order is total and stable. Mirrors the SQL ordering of
/// <c>usp_CollectionsRcv_GetUnits</c> / <c>usp_CollectionsRcv_GetTowers</c> (the equivalence is covered by tests).
/// </summary>
public static class CollectionsTowerOrder
{
    /// <summary>The integer value of a tower number (<c>"140"</c>, <c>"TP140"</c>, <c>" 07 "</c>); null when it is not a plain non-negative integer.</summary>
    public static int? Number(string? towerNumber)
    {
        var value = towerNumber?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith("TP", StringComparison.OrdinalIgnoreCase)) value = value[2..].TrimStart();
        return value.Length is > 0 and <= 9 && value.All(char.IsAsciiDigit) ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The leading integer of the part of a unit code that follows the tower (<c>TP115-1921</c> -> 1921, <c>TP136-C-402</c> -> null); the text itself breaks ties.</summary>
    public static long? UnitNumber(string? unitCode)
    {
        var rest = UnitRest(unitCode);
        return rest.Length is > 0 and <= 15 && rest.All(char.IsAsciiDigit) ? long.Parse(rest, NumberStyles.None, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The unit code after the tower prefix: <c>TP115-1921</c> and <c>115-1921</c> -> <c>1921</c>; a code with no hyphen is returned trimmed.</summary>
    public static string UnitRest(string? unitCode)
    {
        var value = unitCode?.Trim() ?? "";
        var hyphen = value.IndexOf('-');
        return hyphen >= 0 ? value[(hyphen + 1)..].Trim() : value;
    }

    /// <summary>
    /// Tower number DESC (numeric towers first; non-numeric ones after them, by text), then the unit inside the tower (unit number ASC when numeric, numeric units before lettered ones,
    /// then the unit code), then the company, customer, unit id: a total order, so a page never repeats or skips a row.
    /// </summary>
    public static int Compare(string? towerA, string? unitCodeA, int companyA, string? tenantA, long unitIdA,
                              string? towerB, string? unitCodeB, int companyB, string? tenantB, long unitIdB)
    {
        var na = Number(towerA); var nb = Number(towerB);
        if (na.HasValue != nb.HasValue) return na.HasValue ? -1 : 1;
        if (na.HasValue && nb.HasValue && na.Value != nb.Value) return nb.Value.CompareTo(na.Value);   // DESC
        var c = string.CompareOrdinal(na.HasValue ? "" : towerA?.Trim() ?? "", nb.HasValue ? "" : towerB?.Trim() ?? "");
        if (c != 0) return c;
        var ua = UnitNumber(unitCodeA); var ub = UnitNumber(unitCodeB);
        if (ua.HasValue != ub.HasValue) return ua.HasValue ? -1 : 1;
        if (ua.HasValue && ub.HasValue && ua.Value != ub.Value) return ua.Value.CompareTo(ub.Value);
        c = string.CompareOrdinal(UnitRest(unitCodeA), UnitRest(unitCodeB));
        if (c != 0) return c;
        c = string.CompareOrdinal(unitCodeA?.Trim() ?? "", unitCodeB?.Trim() ?? "");
        if (c != 0) return c;
        c = companyA.CompareTo(companyB);
        if (c != 0) return c;
        c = string.CompareOrdinal(tenantA ?? "", tenantB ?? "");
        return c != 0 ? c : unitIdA.CompareTo(unitIdB);
    }

    /// <summary>Tower picker order: number DESC, then the company, then the tower id.</summary>
    public static int CompareTowers(string? numberA, int companyA, long idA, string? numberB, int companyB, long idB)
    {
        var c = Compare(numberA, null, 0, null, 0, numberB, null, 0, null, 0);
        if (c != 0) return c;
        c = companyA.CompareTo(companyB);
        return c != 0 ? c : idA.CompareTo(idB);
    }
}

/// <summary>
/// Whether a stored tower name is a real name. A blank value, or one that only repeats the number (<c>119</c>, <c>TP119</c>), is a placeholder: it carries no information
/// and must never hide the real name that CRM holds. Mirrors the SQL predicate in <c>dbo.fn_Collections_TowerNames</c>.
/// </summary>
public static class CollectionsTowerName
{
    public static bool IsUsable(string? name, string? towerNumber)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) return false;
        var number = towerNumber?.Trim() ?? "";
        if (number.Length == 0) return true;
        return !(n.Equals(number, StringComparison.OrdinalIgnoreCase)
              || n.Equals("TP" + number, StringComparison.OrdinalIgnoreCase)
              || n.Equals("TP-" + number, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What a screen shows for a tower: the real name, else the number alone - never a made-up name.</summary>
    public static string Display(string? name, string? towerNumber, string fallback = "—") =>
        IsUsable(name, towerNumber) ? name!.Trim() : string.IsNullOrWhiteSpace(towerNumber) ? fallback : towerNumber.Trim();
}
