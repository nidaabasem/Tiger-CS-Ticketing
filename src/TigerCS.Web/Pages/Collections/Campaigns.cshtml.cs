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
public sealed class CampaignsModel(CollectionsApiClient api) : PageModel
{
    public string Stage { get; private set; } = "OverdueReminder";
    public DateOnly? BusinessDate { get; private set; }
    public DateOnly? DateFrom { get; private set; }
    public DateOnly? DateTo { get; private set; }
    public int? Month { get; private set; }
    public int? Year { get; private set; }
    public int? TowerId { get; private set; }
    public decimal MinAmount { get; private set; } = ReceivablesModel.DefaultMinAmount;
    public string MinAmountText => MinAmount.ToString("0.####", CultureInfo.InvariantCulture);
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
        int? month = null, int? year = null, decimal? minAmount = null, string? render = null, string? load = null, CancellationToken cancellationToken = default)
    {
        SetFilters(stage, businessDate, towerId, search, pageNumber, dateFrom, dateTo, month, year, minAmount, load);
        RenderFull = render == "full";
        var towers = await api.GetTowersAsync(cancellationToken);
        Towers = towers.IsSuccess && towers.Value is { } list ? list : [];
        TowersFailed = !towers.IsSuccess;
        if (RenderFull) await LoadReportAsync(cancellationToken);
    }

    /// <summary>The results area only (HTML fragment): summary, snapshot status, table, pagination and export links.</summary>
    public async Task<IActionResult> OnGetResultsAsync(string stage = "OverdueReminder", DateOnly? businessDate = null, int? towerId = null,
        string? search = null, [FromQuery(Name = "page")] int pageNumber = 1, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        int? month = null, int? year = null, decimal? minAmount = null, string? load = null, CancellationToken cancellationToken = default)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        SetFilters(stage, businessDate, towerId, search, pageNumber, dateFrom, dateTo, month, year, minAmount, load);
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
        var result = await api.GetCampaignPreviewAsync(Stage, BusinessDate, null, Search, PageNumber, cancellationToken, DateFrom, DateTo, TowerId, MinAmount);
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
        int? month = null, int? year = null, decimal? minAmount = null, CancellationToken cancellationToken = default)
    {
        SetFilters(stage, businessDate, towerId, search, 1, dateFrom, dateTo, month, year, minAmount, null);
        if (!ModelState.IsValid) { Outcome = ApiOutcome.ValidationError; Error = "Choose valid filters and retry."; return Page(); }
        // Same stage, preview date, tower, dates, minimum amount and search as the preview: the file contains exactly the previewed units.
        var result = await api.GetCampaignExportAsync(Stage, mode, BusinessDate, null, Search, cancellationToken, DateFrom, DateTo, TowerId, MinAmount);
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

    private void SetFilters(string stage, DateOnly? date, int? tower, string? search, int page, DateOnly? from, DateOnly? to, int? month, int? year, decimal? minAmount, string? load)
    {
        Stage = stage; BusinessDate = date; TowerId = tower; Search = search?.Trim(); PageNumber = Math.Max(1, page);
        MinAmount = minAmount is >= 0 ? minAmount.Value : ReceivablesModel.DefaultMinAmount;
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

    /// <summary>Defaults the browser re-derives for UNEDITED dates: 1 Jan of the preview year; month end for current-month/follow-up, else the preview date.</summary>
    public DateOnly DefaultFrom => new((BusinessDate ?? Report?.BusinessDate ?? CollectionsDisplay.DubaiToday()).Year, 1, 1);
    public DateOnly DefaultTo => CollectionsEnums.TryParse<CollectionsCampaignStage>(Stage, out var stage)
        ? CollectionsCampaignPolicy.DefaultDateTo(stage, BusinessDate ?? Report?.BusinessDate ?? CollectionsDisplay.DubaiToday())
        : BusinessDate ?? Report?.BusinessDate ?? CollectionsDisplay.DubaiToday();

    public async Task<IActionResult> OnPostLoadCoverageAsync(DateOnly dateFrom, DateOnly dateTo, string? returnUrl, CancellationToken cancellationToken = default)
    {
        var result = await api.RequestCoverageLoadAsync(dateFrom, dateTo, cancellationToken);
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
        query["minAmount"] = MinAmountText;
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
        query["minAmount"] = MinAmountText;
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
        if (export is null) { query["page"] = page.ToString(CultureInfo.InvariantCulture); query["render"] = "full"; }
        else { query["handler"] = "Export"; query["mode"] = export; }
        return $"/Collections/Campaigns?{query}";
    }

    public string ResultsUrl(int page = 1) => PageUrl(page).Replace("render=full", "handler=Results");

    public static string Reason(string reason) => string.Join("; ", reason.Split(';').Select(r => r switch
    {
        "AmbiguousInstalments" => "Several instalments share a due date; amount needs review",
        "MissingUnitIdentity" => "Unit identity needs review",
        "ConflictingContactDetails" => "Customer contact details conflict",
        "UnitAllocationNeedsReview" => "Instalments appear under several units; allocation needs review",
        "ContradictoryPaymentStatus" => "Paid status conflicts with a remaining balance",
        "CurrencyNeedsReview" => "Currency must be AED",
        "AmountPrecisionNeedsReview" => "Amount precision needs review before quoting AED",
        "CoverageIncomplete" => "The selected dates are not fully loaded; load the missing data",
        "StaleSource" => "Receivables data is stale or not fully loaded",
        "SourceReconciliationRequired" => "Financial source reconciliation required",
        "NoValidContact" => "No valid phone or email",
        "OutsideSchedule" => "Preview date is outside today's scheduled campaign",
        "LegalNoticeReleaseRequired" => "Legal notice export is not enabled",
        "InternalLegalReferralOnly" => "Internal Legal review only",
        "Qualifies" => "Qualifies for this stage",
        _ => r
    }));
}
