using System.Globalization;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CampaignsModel(CollectionsApiClient api) : PageModel
{
    public string Stage { get; private set; } = "OverdueReminder";
    public DateOnly? BusinessDate { get; private set; }
    public DateOnly? DateFrom { get; private set; }
    public DateOnly? DateTo { get; private set; }
    public int? CompanyId { get; private set; }
    public string? Search { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public ApiOutcome Outcome { get; private set; }
    public string? Error { get; private set; }
    public CollectionsCampaignPreviewDto? Report { get; private set; }
    public int TotalPages => Report is null ? 1 : Math.Max(1, (int)Math.Ceiling(Report.TotalCount / (double)Report.PageSize));

    public async Task OnGetAsync(string stage = "OverdueReminder", DateOnly? businessDate = null, int? companyId = null,
        string? search = null, [FromQuery(Name = "page")] int pageNumber = 1, CancellationToken cancellationToken = default,
        DateOnly? dateFrom = null, DateOnly? dateTo = null)
    {
        SetFilters(stage, businessDate, companyId, search, pageNumber, dateFrom, dateTo);
        if (!ModelState.IsValid) { Outcome = ApiOutcome.ValidationError; Error = "Choose valid filters and retry."; return; }
        var result = await api.GetCampaignPreviewAsync(Stage, BusinessDate, CompanyId, Search, pageNumber, cancellationToken, DateFrom, DateTo);
        Outcome = result.Outcome;
        Report = result.IsSuccess ? result.Value : null;
        Error = result.Detail;
        BusinessDate ??= Report?.BusinessDate;
        // DateFrom/DateTo stay as typed (blank = stage default), so changing the stage re-derives the default To date.
    }

    public async Task<IActionResult> OnGetExportAsync(string stage = "OverdueReminder", string mode = "review",
        DateOnly? businessDate = null, int? companyId = null, string? search = null,
        CancellationToken cancellationToken = default, DateOnly? dateFrom = null, DateOnly? dateTo = null)
    {
        SetFilters(stage, businessDate, companyId, search, 1, dateFrom, dateTo);
        if (!ModelState.IsValid) { Outcome = ApiOutcome.ValidationError; Error = "Choose valid filters and retry."; return Page(); }
        var result = await api.GetCampaignExportAsync(Stage, mode, BusinessDate, CompanyId, Search, cancellationToken, DateFrom, DateTo);
        if (result.IsSuccess && result.Value is { } file)
        {
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(file.Csv)).ToArray();
            return File(bytes, "text/csv; charset=utf-8", file.FileName);
        }
        Outcome = result.Outcome;
        Error = result.Detail;
        return Page();
    }

    private void SetFilters(string stage, DateOnly? date, int? company, string? search, int page, DateOnly? from, DateOnly? to)
    { Stage = stage; BusinessDate = date; CompanyId = company; Search = search?.Trim(); PageNumber = page; DateFrom = from; DateTo = to; }

    public string PageUrl(int page = 1, string? export = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["stage"] = Stage;
        if (BusinessDate is { } date) query["businessDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateFrom is { } from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateTo is { } to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (CompanyId is { } company) query["companyId"] = company.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
        if (export is null) query["page"] = page.ToString(CultureInfo.InvariantCulture);
        else { query["handler"] = "Export"; query["mode"] = export; }
        return $"/Collections/Campaigns?{query}";
    }

    public static string Reason(string reason) => string.Join("; ", reason.Split(';').Select(r => r switch
    {
        "AmbiguousInstalments" => "Several instalments share a due date; amount needs review",
        "MissingUnitIdentity" => "Unit identity needs review",
        "ConflictingContactDetails" => "Customer contact details conflict",
        "UnitAllocationNeedsReview" => "Instalments appear under several units; allocation needs review",
        "ContradictoryPaymentStatus" => "Paid status conflicts with a remaining balance",
        "CurrencyNeedsReview" => "Currency must be AED",
        "AmountPrecisionNeedsReview" => "Amount precision needs review before quoting AED",
        "StaleSource" => "Financial data is not fresh",
        "SourceReconciliationRequired" => "Financial source reconciliation required",
        "NoValidContact" => "No valid phone or email",
        "OutsideSchedule" => "Preview date is outside today's scheduled campaign",
        "LegalNoticeReleaseRequired" => "Legal notice export is not enabled",
        "InternalLegalReferralOnly" => "Internal Legal review only",
        "Qualifies" => "Qualifies for this stage",
        _ => r
    }));
}
