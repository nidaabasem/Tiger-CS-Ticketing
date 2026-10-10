using System.Globalization;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CampaignsModel(CollectionsApiClient api, Microsoft.Extensions.Configuration.IConfiguration configuration) : PageModel
{
    public string Stage { get; private set; } = "OverdueReminder";
    public DateOnly? BusinessDate { get; private set; }
    public DateOnly? DateFrom { get; private set; }
    public DateOnly? DateTo { get; private set; }
    public int? Month { get; private set; }
    public int? Year { get; private set; }
    public int? TowerId { get; private set; }
    /// <summary>Minimum Total (AED): a unit is listed only when its Due + Overdue is greater than this. Default 100; null (cleared) = every unit that has a Due or Overdue amount.</summary>
    public decimal? MinTotal { get; private set; } = ReceivablesModel.DefaultMinTotal;
    public string MinTotalText => MinTotal?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
    /// <summary>The chosen dates start after today: nothing is due yet, so no data call is made.</summary>
    public bool NothingDueYet { get; private set; }
    public IReadOnlyList<CollectionsTowerDto> Towers { get; private set; } = [];
    public bool TowersFailed { get; private set; }
    public string? Search { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public ApiOutcome Outcome { get; private set; }
    public string? Error { get; private set; }
    public bool RenderFull { get; private set; }
    public CollectionsCampaignPreviewDto? Report { get; private set; }
    public int TotalPages => Report is null ? 1 : Math.Max(1, (int)Math.Ceiling(Report.TotalCount / (double)Report.PageSize));
    public static readonly string[] MonthNames = ReceivablesModel.MonthNames;
    public IEnumerable<int> YearOptions()
    {
        var current = CollectionsDisplay.DubaiToday().Year;
        var first = Math.Min(2020, Year ?? 2020);
        var last = Math.Max(current + 2, Year ?? 0);
        return Enumerable.Range(first, last - first + 1);
    }

    /// <summary>The page (filters + an empty results area) renders at once and never waits for data; results come from <see cref="OnGetResultsAsync"/>.</summary>
    public async Task OnGetAsync(string stage = "OverdueReminder", DateOnly? businessDate = null, int? towerId = null,
        string? search = null, [FromQuery(Name = "page")] int pageNumber = 1, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        int? month = null, int? year = null, string? render = null, string? load = null, CancellationToken cancellationToken = default)
    {
        SetFilters(stage, businessDate, towerId, search, pageNumber, dateFrom, dateTo, month, year, load);
        RenderFull = render == "full";
        var towers = await api.GetTowersAsync(cancellationToken);
        Towers = towers.IsSuccess && towers.Value is { } list ? list : [];
        TowersFailed = !towers.IsSuccess;
        if (RenderFull) await LoadReportAsync(cancellationToken);
    }

    /// <summary>The results area only (HTML fragment): summary, snapshot status, table, pagination and export links.</summary>
    public async Task<IActionResult> OnGetResultsAsync(string stage = "OverdueReminder", DateOnly? businessDate = null, int? towerId = null,
        string? search = null, [FromQuery(Name = "page")] int pageNumber = 1, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        int? month = null, int? year = null, string? load = null, CancellationToken cancellationToken = default)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        SetFilters(stage, businessDate, towerId, search, pageNumber, dateFrom, dateTo, month, year, load);
        await LoadReportAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        var t = Report?.Timings;
        Response.Headers["Server-Timing"] = string.Create(CultureInfo.InvariantCulture,
            $"web;dur={timer.Elapsed.TotalMilliseconds:F0}, sql;dur={t?.SourceMs ?? 0:F0}, map;dur={t?.MapMs ?? 0:F0}");
        return Partial("_CampaignResults", this);
    }

    private async Task LoadReportAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) { Outcome = ApiOutcome.ValidationError; Error = "Choose valid filters and retry."; return; }
        // Instalments due after today are never listed: a period that starts after today has nothing to ask the API for.
        if (DateFrom is { } start && start > CollectionsDisplay.DubaiToday()) { NothingDueYet = true; return; }
        var result = await api.GetCampaignPreviewAsync(Stage, BusinessDate, null, Search, PageNumber, cancellationToken, DateFrom, DateTo, TowerId, MinTotal);
        Outcome = result.Outcome;
        Report = result.IsSuccess ? result.Value : null;
        Error = result.Detail;
        BusinessDate ??= Report?.BusinessDate;
        // The inputs show the effective window. Unedited defaults follow stage / preview-date changes in the browser
        // (collections-campaign-dates.js, same rules as CollectionsCampaignPolicy.DefaultDateTo); an edited date is kept.
        DateFrom ??= Report?.DateFrom;
        DateTo ??= Report?.DateTo;
        SyncMonthYear();
    }

    public async Task<IActionResult> OnGetExportAsync(string stage = "OverdueReminder", string mode = "review",
        DateOnly? businessDate = null, int? towerId = null, string? search = null, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        int? month = null, int? year = null, CancellationToken cancellationToken = default)
    {
        SetFilters(stage, businessDate, towerId, search, 1, dateFrom, dateTo, month, year, null);
        if (!ModelState.IsValid) { Outcome = ApiOutcome.ValidationError; Error = "Choose valid filters and retry."; return Page(); }
        // Same stage, preview date, tower, dates, minimum amount and search as the preview: the file contains exactly the previewed units.
        var result = await api.GetCampaignExportAsync(Stage, mode, BusinessDate, null, Search, cancellationToken, DateFrom, DateTo, TowerId, MinTotal);
        if (result.IsSuccess && result.Value is { } file)
        {
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(file.Csv)).ToArray();
            return File(bytes, "text/csv; charset=utf-8", file.FileName);
        }
        Outcome = result.Outcome;
        Error = result.Detail;
        RenderFull = true;
        return Page();
    }

    private void SetFilters(string stage, DateOnly? date, int? tower, string? search, int page, DateOnly? from, DateOnly? to, int? month, int? year, string? load)
    {
        Stage = stage; BusinessDate = date; TowerId = tower; Search = search?.Trim(); PageNumber = Math.Max(1, page);
        MinTotal = ReceivablesModel.ParseMinTotal(Request.Query);
        LoadNotice = CollectionsDisplay.NoticeText(load) is { Length: > 0 } text ? text : null;
        if (month is >= 1 and <= 12 && year is >= 2000 and <= 2100) { (DateFrom, DateTo) = CollectionsDateRanges.Month(year.Value, month.Value); }
        else { DateFrom = from; DateTo = to; }
        SyncMonthYear();
    }

    private void SyncMonthYear()
    {
        if (DateFrom is { } f && DateTo is { } t && CollectionsDateRanges.TryAsCalendarMonth(f, t, out var y, out var m)) { Year = y; Month = m; }
        else { Year = null; Month = null; }
    }

    public string? LoadNotice { get; private set; }

    /// <summary>The configured receivables start date (CollectionsSource:PactReceivables:StartDate, as in the API; 2026-01-01 when absent). Overdue and legal stages
    /// look back months, so an unedited From is this date and not 1 January of the preview year - the same default the API applies to a blank From.</summary>
    public DateOnly ConfiguredStart => DateTime.TryParse(configuration["CollectionsSource:PactReceivables:StartDate"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        ? DateOnly.FromDateTime(d) : DateOnly.FromDateTime(new TigerCS.Application.Modules.Collections.PactReceivablesOptions().StartDate);

    /// <summary>Defaults the browser re-derives for UNEDITED dates: the configured start date; month end for current-month/follow-up, else the preview date.</summary>
    public DateOnly DefaultFrom => ConfiguredStart;
    public DateOnly DefaultTo => CollectionsEnums.TryParse<CollectionsCampaignStage>(Stage, out var stage)
        ? CollectionsCampaignPolicy.DefaultDateTo(stage, BusinessDate ?? Report?.BusinessDate ?? CollectionsDisplay.DubaiToday())
        : BusinessDate ?? Report?.BusinessDate ?? CollectionsDisplay.DubaiToday();

    public async Task<IActionResult> OnPostLoadCoverageAsync(DateOnly dateFrom, DateOnly dateTo, string? returnUrl, int? companyId = null, CancellationToken cancellationToken = default)
    {
        var result = await api.RequestCoverageLoadAsync(dateFrom, dateTo, cancellationToken, companyId);
        var target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/Collections/Campaigns";
        return LocalRedirect(CollectionsDisplay.WithNotice(target, CollectionsDisplay.NoticeCode(result)));
    }

    /// <summary>"Last 6 months": six calendar months before the preview date through the preview date (explicit, so it overrides the stage default).</summary>
    public string LastSixMonthsUrl()
    {
        var preview = BusinessDate ?? Report?.BusinessDate ?? CollectionsDisplay.DubaiToday();
        var (from, to) = CollectionsDateRanges.LastSixMonths(preview);
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["stage"] = Stage;
        query["businessDate"] = preview.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (TowerId is { } tower) query["towerId"] = tower.ToString(CultureInfo.InvariantCulture);
        query["minTotal"] = MinTotalText;
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
        return $"/Collections/Campaigns?{query}";
    }

    public string PageUrl(int page = 1, string? export = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["stage"] = Stage;
        if (BusinessDate is { } date) query["businessDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateFrom is { } from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateTo is { } to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (TowerId is { } tower) query["towerId"] = tower.ToString(CultureInfo.InvariantCulture);
        query["minTotal"] = MinTotalText;
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
        if (export is null) { query["page"] = page.ToString(CultureInfo.InvariantCulture); query["render"] = "full"; }
        else { query["handler"] = "Export"; query["mode"] = export; }
        return $"/Collections/Campaigns?{query}";
    }

    public string ResultsUrl(int page = 1) => PageUrl(page).Replace("render=full", "handler=Results");

    public static string MoneyOrDash(decimal amount) => amount > 0 ? amount.ToString("N2", CultureInfo.InvariantCulture) : "—";
}
