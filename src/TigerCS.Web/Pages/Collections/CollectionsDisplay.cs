using System.Globalization;
using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Web.Pages.Collections;

/// <summary>Display helpers shared by the Receivables and Campaigns pages.</summary>
public static class CollectionsDisplay
{
    /// <summary>Gulf Standard Time has no daylight saving, so a fixed +4 h offset is exact.</summary>
    public static string DubaiTime(DateTime? utc) => utc is { } value
        ? value.AddHours(4).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " (Dubai)"
        : "never";

    public static string FreshnessLabel(SnapshotCompanyStatusDto company) => company.Freshness switch
    {
        "Fresh" => "Fresh",
        "Stale" => "Stale",
        _ => "Not loaded"
    };

    /// <summary>Tower option label: number and name.</summary>
    public static string TowerLabel(CollectionsTowerDto tower) => $"{tower.TowerNumber} - {tower.TowerName}";

    public static string UnmatchedReason(string reason) => reason switch
    {
        "NoMatchingTower" => "not in the tower list",
        "InactiveTower" => "tower is inactive",
        "NoTowerNumber" => "no tower number in the unit code",
        _ => reason
    };

    public static string Money(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);
}
