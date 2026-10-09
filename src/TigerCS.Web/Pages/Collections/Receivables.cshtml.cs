using System.Diagnostics;
using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

/// <summary>
/// Instalment-level Receivables. The page itself (filters + an empty results area) renders immediately and never waits for data; the results are
/// fetched from the local snapshot by <see cref="OnGetResultsAsync"/> (HTML fragment, called by receivables.js) so only the results area shows loading.
/// </summary>
public sealed class ReceivablesModel(CollectionsApiClient api) : PageModel
{
    /// <summary>Same as Collections:... PactReceivables:DefaultMinOutstandingAmount on the API; the form always sends the value explicitly.</summary>
    public const decimal DefaultMinAmount = 100m;

    public int? TowerId { get; private set; }
    public DateOnly? DateFrom { get; private set; }
    public DateOnly? DateTo { get; private set; }
    public int? Month { get; private set; }
    public int? Year { get; private set; }
    public string PaymentStatus { get; private set; } = "outstanding";
    public decimal MinAmount { get; private set; } = DefaultMinAmount;
    public string? Search { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public IReadOnlyList<CollectionsTowerDto> Towers { get; private set; } = [];
    public bool TowersFailed { get; private set; }
    public ApiOutcome Outcome { get; private set; }
    public string? Error { get; private set; }
    public string? LoadNotice { get; private set; }
    public PactInstalmentsPageDto? Report { get; private set; }
    public bool RenderFull { get; private set; }
    public int TotalPages => Report is null ? 1 : Math.Max(1, (int)Math.Ceiling(Report.Totals.Count / (double)Report.PageSize));
    public bool MinApplies => CollectionsPaymentFilters.TryParse(PaymentStatus, out var f) && CollectionsPaymentFilters.MinimumApplies(f);
    public string MinAmountText => MinAmount.ToString("0.####", CultureInfo.InvariantCulture);

    public static readonly IReadOnlyList<(string Value, string Label)> PaymentOptions =
    [
        ("outstanding", "Outstanding (unpaid + partially paid)"), ("unpaid", "Unpaid"), ("partial", "Partially paid"), ("paid", "Fully paid"), ("all", "All")
    ];

    public static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Take(12).ToArray();

    public IEnumerable<int> YearOptions()
    {
        var current = CollectionsDisplay.DubaiToday().Year;
        var first = Math.Min(2020, Year ?? 2020);
        var last = Math.Max(current + 2, Year ?? 0);
        return Enumerable.Range(first, last - first + 1);
    }

    public async Task OnGetAsync(int? towerId, DateOnly? dateFrom, DateOnly? dateTo, int? month, int? year, string? paymentStatus, decimal? minAmount,
        string? search, int page = 1, string? render = null, string? load = null, CancellationToken cancellationToken = default)
    {
        Bind(towerId, dateFrom, dateTo, month, year, paymentStatus, minAmount, search, page, load);
        RenderFull = render == "full";
        // The shell never waits for data. The tower list is a small local read; results arrive through the Results handler (or render=full without JavaScript).
        var towers = await api.GetTowersAsync(cancellationToken);
        Towers = towers.IsSuccess && towers.Value is { } list ? list : [];
        TowersFailed = !towers.IsSuccess;
        if (RenderFull) await LoadReportAsync(cancellationToken);
    }

    /// <summary>The results area only (HTML fragment).</summary>
    public async Task<IActionResult> OnGetResultsAsync(int? towerId, DateOnly? dateFrom, DateOnly? dateTo, int? month, int? year, string? paymentStatus, decimal? minAmount,
        string? search, int page = 1, string? load = null, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        Bind(towerId, dateFrom, dateTo, month, year, paymentStatus, minAmount, search, page, load);
        var apiTimer = Stopwatch.StartNew();
        await LoadReportAsync(cancellationToken);
        var apiMs = apiTimer.Elapsed.TotalMilliseconds;
        Response.Headers.CacheControl = "no-store";
        var t = Report?.Timings;
        Response.Headers["Server-Timing"] = string.Create(CultureInfo.InvariantCulture,
            $"web;dur={timer.Elapsed.TotalMilliseconds:F0}, api;dur={apiMs:F0}, sql;dur={t?.SourceMs ?? 0:F0}, map;dur={t?.MapMs ?? 0:F0}");
        return Partial("_InstalmentResults", this);
    }

    private void Bind(int? towerId, DateOnly? dateFrom, DateOnly? dateTo, int? month, int? year, string? paymentStatus, decimal? minAmount, string? search, int page, string? load)
    {
        TowerId = towerId;
        PaymentStatus = CollectionsPaymentFilters.TryParse(paymentStatus, out var filter) ? CollectionsPaymentFilters.ToWire(filter) : "outstanding";
        MinAmount = minAmount is >= 0 ? minAmount.Value : DefaultMinAmount;
        Search = search?.Trim();
        PageNumber = Math.Max(1, page);
        LoadNotice = CollectionsDisplay.NoticeText(load) is { Length: > 0 } text ? text : null;
        // A chosen Month + Year define the range (first to last day of that month); otherwise the From / To dates are used as typed.
        if (month is >= 1 and <= 12 && year is >= 2000 and <= 2100)
        {
            (var first, var last) = CollectionsDateRanges.Month(year.Value, month.Value);
            DateFrom = first; DateTo = last;
        }
        else { DateFrom = dateFrom; DateTo = dateTo; }
        SyncMonthYear();
    }

    private void SyncMonthYear()
    {
        if (DateFrom is { } f && DateTo is { } t && CollectionsDateRanges.TryAsCalendarMonth(f, t, out var y, out var m)) { Year = y; Month = m; }
        else { Year = null; Month = null; }
    }

    private async Task LoadReportAsync(CancellationToken cancellationToken)
    {
        var result = await api.GetInstalmentsAsync(TowerId, DateFrom, DateTo, PaymentStatus, MinApplies ? MinAmount : null, Search, PageNumber, cancellationToken);
        Outcome = result.Outcome;
        Error = result.Detail;
        Report = result.IsSuccess ? result.Value : null;
        if (Report is not null)
        {
            // Show the effective window (the API default when none was typed) in the inputs.
            DateFrom ??= Report.DateFrom; DateTo ??= Report.DateTo;
            SyncMonthYear();
        }
    }

    /// <summary>Query string of the current filters (page 1 unless given); the same names the form submits.</summary>
    public string Query(int? page = null, DateOnly? from = null, DateOnly? to = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (TowerId is { } tower) query["towerId"] = tower.ToString(CultureInfo.InvariantCulture);
        if ((from ?? DateFrom) is { } f) query["dateFrom"] = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if ((to ?? DateTo) is { } t) query["dateTo"] = t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        query["paymentStatus"] = PaymentStatus;
        query["minAmount"] = MinAmountText;
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
        if (page is > 1) query["page"] = page.Value.ToString(CultureInfo.InvariantCulture);
        return query.ToString()!;
    }

    public string PageUrl(int page) => $"/Collections/Receivables?{Query(page)}&render=full";
    public string ResultsUrl(int page) => $"/Collections/Receivables?{Query(page)}&handler=Results";

    /// <summary>Starts the background load of the missing range (no-JavaScript fallback; receivables.js posts the same form with fetch).</summary>
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
        return $"/Collections/Receivables?{Query(null, from, to)}";
    }

    public static string Money(decimal? amount) => amount is { } value ? value.ToString("N2", CultureInfo.InvariantCulture) : "—";

    public static string StatusLabel(string status) => status switch
    {
        "Unpaid" => "Unpaid", "PartiallyPaid" => "Partially paid", "FullyPaid" => "Fully paid", _ => "Needs verification"
    };

    public static string ClassificationLabel(string classification) => classification switch
    {
        "Overdue" => "Overdue", "Due" => "Due", "NotYetDue" => "Not yet due", _ => "—"
    };
}
