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
    /// <summary>Due, Overdue or Both (default). Sent to the API with every preview, page and export so they always agree.</summary>
    public string PaymentStatus { get; private set; } = "Both";
    public int? TowerId { get; private set; }
    /// <summary>Minimum Total (AED): a unit is listed only when its Due + Overdue is greater than this. Default 100; null (cleared) = every unit that has a Due or Overdue amount.</summary>
    public decimal? MinTotal { get; private set; } = ReceivablesModel.DefaultMinTotal;
    public string MinTotalText => MinTotal?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
    /// <summary>The chosen dates start in a month after the current one: neither Due nor Overdue, so no data call is made.</summary>
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
    public static readonly string[] PaymentStatusChoices = ["Both", "Due", "Overdue"];

    /// <summary>The page (filters + an empty results area) renders at once and never waits for data; results come from <see cref="OnGetResultsAsync"/>.</summary>
    public async Task OnGetAsync(string stage = "OverdueReminder", DateOnly? businessDate = null, int? towerId = null,
        string? search = null, [FromQuery(Name = "page")] int pageNumber = 1, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        int? month = null, int? year = null, string? render = null, string? load = null, string? paymentStatus = null, CancellationToken cancellationToken = default)
    {
        SetFilters(stage, businessDate, towerId, search, pageNumber, dateFrom, dateTo, month, year, load, paymentStatus);
        RenderFull = render == "full";
        var towers = await api.GetTowersAsync(cancellationToken);
        Towers = towers.IsSuccess && towers.Value is { } list ? list : [];
        TowersFailed = !towers.IsSuccess;
        if (RenderFull) await LoadReportAsync(cancellationToken);
    }

    /// <summary>The results area only (HTML fragment): summary, snapshot status, table, pagination and export links.</summary>
    public async Task<IActionResult> OnGetResultsAsync(string stage = "OverdueReminder", DateOnly? businessDate = null, int? towerId = null,
        string? search = null, [FromQuery(Name = "page")] int pageNumber = 1, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        int? month = null, int? year = null, string? load = null, string? paymentStatus = null, CancellationToken cancellationToken = default)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        SetFilters(stage, businessDate, towerId, search, pageNumber, dateFrom, dateTo, month, year, load, paymentStatus);
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
        // Instalments due from the next month on are neither Due nor Overdue and never listed: a period that starts there has nothing to ask the API for.
        if (DateFrom is { } start && !CollectionsDueMonth.IsInScope(start, CollectionsDisplay.DubaiToday())) { NothingDueYet = true; return; }
        var result = await api.GetCampaignPreviewAsync(Stage, BusinessDate, null, Search, PageNumber, cancellationToken, DateFrom, DateTo, TowerId, MinTotal, PaymentStatus);
        Outcome = result.Outcome;
        Report = result.IsSuccess ? result.Value : null;
        Error = result.Detail;
        BusinessDate ??= Report?.BusinessDate;
        // To shows the effective end. From stays EMPTY unless the user chose it: empty = All months = the whole history up to the end of the current month (the read scope); the stage
        // eligibility scope is unchanged. Writing the effective 2000-01-01 back into the input would turn the next page / export request into an explicit From
        // and widen the stage eligibility, so it is never done. Unedited To follows stage / preview-date changes in the browser (collections-campaign-dates.js).
        DateTo ??= Report?.DateTo;
    }

    public async Task<IActionResult> OnGetExportAsync(string stage = "OverdueReminder", string mode = "review",
        DateOnly? businessDate = null, int? towerId = null, string? search = null, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        int? month = null, int? year = null, string? paymentStatus = null, CancellationToken cancellationToken = default)
    {
        SetFilters(stage, businessDate, towerId, search, 1, dateFrom, dateTo, month, year, null, paymentStatus);
        if (!ModelState.IsValid) { Outcome = ApiOutcome.ValidationError; Error = "Choose valid filters and retry."; return Page(); }
        // Same stage, preview date, tower, dates, minimum amount and search as the preview: the file contains exactly the previewed units.
        var result = await api.GetCampaignExportAsync(Stage, mode, BusinessDate, null, Search, cancellationToken, DateFrom, DateTo, TowerId, MinTotal, PaymentStatus);
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

    private void SetFilters(string stage, DateOnly? date, int? tower, string? search, int page, DateOnly? from, DateOnly? to, int? month, int? year, string? load, string? paymentStatus)
    {
        PaymentStatus = CampaignPaymentFilters.TryParse(paymentStatus, out var payment) ? CampaignPaymentFilters.Name(payment) : paymentStatus!.Trim();
        Stage = stage; BusinessDate = date; TowerId = tower; Search = search?.Trim(); PageNumber = Math.Max(1, page);
        MinTotal = ReceivablesModel.ParseMinTotal(Request.Query);
        LoadNotice = CollectionsDisplay.NoticeText(load) is { Length: > 0 } text ? text : null;
        // Month / Year are no longer controls; an old bookmark that still carries them keeps working as its whole-month range.
        if (month is >= 1 and <= 12 && year is >= 2000 and <= 2100) { (DateFrom, DateTo) = CollectionsDateRanges.Month(year.Value, month.Value); }
        else { DateFrom = from; DateTo = to; }
    }

    public string? LoadNotice { get; private set; }

    /// <summary>The configured receivables start date (CollectionsSource:PactReceivables:StartDate, as in the API; 2026-01-01 when absent). Overdue and legal stages
    /// look back months, so an unedited From is this date and not 1 January of the preview year - the same default the API applies to a blank From.</summary>
    public DateOnly ConfiguredStart => DateTime.TryParse(configuration["CollectionsSource:PactReceivables:StartDate"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        ? DateOnly.FromDateTime(d) : DateOnly.FromDateTime(new TigerCS.Application.Modules.Collections.PactReceivablesOptions().StartDate);

    /// <summary>Defaults the browser re-derives for UNEDITED dates: From is empty (All months = the whole history); To is the month end for current-month/follow-up, else the preview date.
    /// The configured start date (<see cref="ConfiguredStart"/>) only bounds which units are ELIGIBLE for a stage; it is not shown as the From date.</summary>
    public DateOnly? DefaultFrom => null;
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
        query["paymentStatus"] = PaymentStatus;
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
        query["paymentStatus"] = PaymentStatus;
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
        if (export is null) { query["page"] = page.ToString(CultureInfo.InvariantCulture); query["render"] = "full"; }
        else { query["handler"] = "Export"; query["mode"] = export; }
        return $"/Collections/Campaigns?{query}";
    }

    /// <summary>
    /// Review &amp; approve for exactly this selection: the stage, the explicit due-date range and the Due / Overdue status travel with the link, so the review list starts from the
    /// filters the preview used. (The Review page keeps its own project text and amount filters; the tower is not a review filter.)
    /// </summary>
    public string ReviewUrl()
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (CollectionsEnums.TryParse<CollectionsCampaignStage>(Stage, out var stage))
            query["reminderType"] = TigerCS.Domain.Modules.Collections.Review.CampaignReminderTypes.FromStage(stage).ToString();
        if (DateFrom is { } from) query["dueFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateTo is { } to) query["dueTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (PaymentStatus is not "Both") query["dueStatus"] = PaymentStatus;
        if (TowerId is { } tower) query["towerId"] = tower.ToString(CultureInfo.InvariantCulture);
        return query.Count == 0 ? "/Collections/Review" : $"/Collections/Review?{query}";
    }

    /// <summary>The stage's display name = the Genesys campaign name used when this list is sent.</summary>
    public string? CampaignName => CollectionsEnums.TryParse<CollectionsCampaignStage>(Stage, out var stage)
        ? TigerCS.Domain.Modules.Collections.Review.CampaignReminderTypes.StageName(stage) : null;

    public string ResultsUrl(int page = 1) => PageUrl(page).Replace("render=full", "handler=Results");

    /// <summary>Short review notes of a unit's CRM / PACT linking (the reasons the unit cannot be trusted as one customer).</summary>
    public static IEnumerable<string> ReviewNotes(CollectionsCampaignContactDto row, string? crmState = null)
    {
        if (row.Reason.Contains("CrmCustomerAmbiguous", StringComparison.Ordinal)) yield return "Several CRM customers: needs review";
        if (row.Reason.Contains("CrmCompanyAmbiguous", StringComparison.Ordinal)) yield return "CRM sale cannot be tied to one company: needs review";
        if (row.Reason.Contains("ContactSourceConflict", StringComparison.Ordinal)) yield return "CRM and PACT contacts differ: needs review";
        if (row.Reason.Contains("CrmUnavailable", StringComparison.Ordinal)) yield return "CRM unavailable: contact unconfirmed, needs review";
        // When CRM could not be used the reason above already says the contact is unconfirmed; it must not also claim that CRM holds nothing.
        if (row.Reason.Contains("NoValidContact", StringComparison.Ordinal) && !row.Reason.Contains("CrmUnavailable", StringComparison.Ordinal)) yield return NoContactNote(row, crmState);
    }

    /// <summary>Why a unit cannot be sent: "CRM or PACT" is only claimed when CRM was consulted and linked this unit; a switched-off feed or an unlinked owner is said as such.</summary>
    public static string NoContactNote(CollectionsCampaignContactDto row, string? crmState)
    {
        if (crmState == "Disabled") return "No mobile or email in PACT; CRM linking is switched off, so CRM was not checked: cannot be sent";
        var ambiguous = row.Reason.Contains("CrmCustomerAmbiguous", StringComparison.Ordinal) || row.Reason.Contains("CrmCompanyAmbiguous", StringComparison.Ordinal);
        if (crmState is not null && row.CrmCustomerId is null && !ambiguous) return "No mobile or email in PACT; no CRM owner is linked to this unit: cannot be sent";
        return "No mobile or email in CRM or PACT: cannot be sent";
    }

    /// <summary>The note under an empty mobile: when CRM could not be used it says so instead of claiming the customer has no mobile.</summary>
    public static string NoMobileNote(CollectionsCampaignContactDto row) =>
        row.Reason.Contains("CrmUnavailable", StringComparison.Ordinal) ? "Mobile unconfirmed: CRM unavailable" : "No mobile: voice / SMS not possible";

    /// <summary>Readiness to send. It never changes the displayed balances: a unit the stage rule does not make eligible keeps its Due / Overdue / Total and only says why it is not ready.</summary>
    public static string Readiness(CollectionsCampaignContactDto row) => row.Status switch
    {
        "Ready" => "Ready",
        "NeedsReview" => "Needs review",
        "PreviewOnly" => "Preview only",
        "InternalReview" => "Internal review",
        "NotEligible" => row.Reason.Contains("BelowThreshold", StringComparison.Ordinal) ? "Not eligible: below this stage's threshold" : "Not eligible: no balance qualifies for this stage",
        _ => row.Status,
    };

    public static string MoneyOrDash(decimal amount) => amount > 0 ? amount.ToString("N2", CultureInfo.InvariantCulture) : "—";
}
