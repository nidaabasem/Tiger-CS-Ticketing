using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

public sealed class ReceivablesModel(CollectionsApiClient api) : PageModel
{
    public int? CompanyId { get; private set; }
    public string? Status { get; private set; }
    public string? Search { get; private set; }
    public int? Year { get; private set; }
    public int? Month { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public ApiOutcome Outcome { get; private set; }
    public PactReceivableCustomersDto? Report { get; private set; }
    public int TotalPages => Report is null ? 1 : Math.Max(1, (int)Math.Ceiling(Report.TotalCount / (double)Report.PageSize));

    public async Task OnGetAsync(int? companyId, string? status, string? search, int page = 1, int? year = null, int? month = null,
        CancellationToken cancellationToken = default)
    {
        CompanyId = companyId;
        Status = status?.Trim().ToLowerInvariant();
        Search = search?.Trim();
        Year = year;
        Month = month;
        PageNumber = page;
        var result = await api.GetReceivableCustomersAsync(companyId, Status, Search, page, cancellationToken, year, month);
        Outcome = result.Outcome;
        Report = result.IsSuccess ? result.Value : null;
    }

    public string PageUrl(int page)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (CompanyId is { } company) query["companyId"] = company.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(Status)) query["status"] = Status;
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
        if (Year is { } y && Month is { } m) { query["year"] = y.ToString(CultureInfo.InvariantCulture); query["month"] = m.ToString(CultureInfo.InvariantCulture); }
        query["page"] = page.ToString(CultureInfo.InvariantCulture);
        return $"/Collections/Receivables?{query}";
    }

    public static string PaymentLabel(string status) => status switch
    {
        "Unpaid" => "Unpaid", "PartiallyPaid" => "Partially Paid", "Paid" => "Paid", _ => "Unknown"
    };

    public static string TimingLabel(string timing) => timing switch
    {
        "DueToday" => "Due Today", "Overdue" => "Overdue", "Upcoming" => "Upcoming", _ => "Unknown"
    };

    public static string Money(decimal? amount) => amount is { } value
        ? value.ToString("N2", CultureInfo.InvariantCulture) : "Review needed";
}
