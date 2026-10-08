using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

public sealed class ReceivablesModel(CollectionsApiClient api) : PageModel
{
    public int? TowerId { get; private set; }
    public DateOnly? DateFrom { get; private set; }
    public DateOnly? DateTo { get; private set; }
    public IReadOnlyList<CollectionsTowerDto> Towers { get; private set; } = [];
    public bool TowersFailed { get; private set; }
    public string? Status { get; private set; }
    public string? Search { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public ApiOutcome Outcome { get; private set; }
    public PactReceivableCustomersDto? Report { get; private set; }
    public int TotalPages => Report is null ? 1 : Math.Max(1, (int)Math.Ceiling(Report.TotalCount / (double)Report.PageSize));

    /// <summary>Tower + From + To only: the company is resolved from the tower on the server, so there is no company filter here.</summary>
    public string? LoadNotice { get; private set; }

    public async Task OnGetAsync(int? towerId, DateOnly? dateFrom, DateOnly? dateTo, string? status, string? search, int page = 1,
        CancellationToken cancellationToken = default, string? load = null)
    {
        LoadNotice = CollectionsDisplay.NoticeText(load) is { Length: > 0 } text ? text : null;
        TowerId = towerId;
        DateFrom = dateFrom;
        DateTo = dateTo;
        Status = status?.Trim().ToLowerInvariant();
        Search = search?.Trim();
        PageNumber = page;
        var towersTask = api.GetTowersAsync(cancellationToken);
        var result = await api.GetReceivableCustomersAsync(null, Status, Search, page, cancellationToken, null, null, towerId, dateFrom, dateTo);
        var towers = await towersTask;
        Towers = towers.IsSuccess && towers.Value is { } list ? list : [];
        TowersFailed = !towers.IsSuccess;
        Outcome = result.Outcome;
        Error = result.Detail;
        Report = result.IsSuccess ? result.Value : null;
        // Show the effective window (the defaults the API applied) so both dates are visible and editable.
        DateFrom ??= Report?.DateFrom;
        DateTo ??= Report?.DateTo;
    }

    public string? Error { get; private set; }

    /// <summary>Starts the background load of the missing range, then returns to the same filtered page with a notice.</summary>
    public async Task<IActionResult> OnPostLoadCoverageAsync(DateOnly dateFrom, DateOnly dateTo, string? returnUrl, CancellationToken cancellationToken = default)
    {
        var result = await api.RequestCoverageLoadAsync(dateFrom, dateTo, cancellationToken);
        var target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/Collections/Receivables";
        return LocalRedirect(CollectionsDisplay.WithNotice(target, CollectionsDisplay.NoticeCode(result)));
    }

    /// <summary>"Last 6 months": six calendar months before the preview date (today, Dubai) through the preview date.</summary>
    public string LastSixMonthsUrl()
    {
        var (from, to) = CollectionsDateRanges.LastSixMonths(Report?.BusinessDate ?? CollectionsDisplay.DubaiToday());
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (TowerId is { } tower) query["towerId"] = tower.ToString(CultureInfo.InvariantCulture);
        query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(Status)) query["status"] = Status;
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
        return $"/Collections/Receivables?{query}";
    }

    public string PageUrl(int page)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (TowerId is { } tower) query["towerId"] = tower.ToString(CultureInfo.InvariantCulture);
        if (DateFrom is { } from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateTo is { } to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(Status)) query["status"] = Status;
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
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
