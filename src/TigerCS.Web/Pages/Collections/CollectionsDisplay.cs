using System.Globalization;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Web.Services.Api;

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

    /// <summary>Today in Dubai (UTC+4, no DST): the preview date when a page has no report to take it from.</summary>
    public static DateOnly DubaiToday() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4));

    public static string NoticeText(string? code) => code switch
    {
        "started" => "Loading the missing data in the background. Reload this page in a few minutes; the list stays marked incomplete until it finishes.",
        "running" => "A load is already running. Reload this page in a few minutes.",
        "covered" => "The selected dates are already loaded and fresh.",
        "failed" => "The load could not be started. Retry, or ask an administrator to check the receivables refresh.",
        _ => ""
    };

    /// <summary>The current local URL with the one-shot load notice replaced (open redirects are refused by the caller).</summary>
    public static string WithNotice(string localUrl, string code)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(localUrl, @"([?&])load=[^&]*&?", "$1").TrimEnd('?', '&');
        return cleaned + (cleaned.Contains('?') ? "&" : "?") + "load=" + code;
    }

    public static string NoticeCode(ApiResult<ReceivablesRangeLoadDto> result) => !result.IsSuccess ? "failed"
        : result.Value!.Accepted ? "started" : result.Value.AlreadyRunning ? "running" : result.Value.AlreadyCovered ? "covered" : "failed";
}