using System.Diagnostics;
using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

/// <summary>
/// Receivables: one row per unit (company + customer + unit) with everything the unit has not paid yet; View Details opens that unit's unpaid instalments.
/// The page itself (filters + an empty results area) renders immediately and never waits for data; the results are fetched from the local snapshot by
/// <see cref="OnGetResultsAsync"/> (HTML fragment, called by receivables.js) so only the results area shows loading.
/// </summary>
public sealed class ReceivablesModel(CollectionsApiClient api) : PageModel
{
    /// <summary>Default of Minimum Total (AED): units whose Due + Overdue is not greater than it are hidden. The user can change or clear it.</summary>
    public const decimal DefaultMinTotal = 100m;

    /// <summary>
    /// The Minimum Total of a request: absent = the default (100); present but empty = cleared (null: every unit with a Due or Overdue amount); otherwise the number.
    /// Read from the raw query because an empty value and a missing one both bind to null.
    /// </summary>
    public static decimal? ParseMinTotal(Microsoft.AspNetCore.Http.IQueryCollection query)
    {
        if (!query.TryGetValue("minTotal", out var raw)) return DefaultMinTotal;
        var text = raw.ToString().Trim();
        if (text.Length == 0) return null;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : DefaultMinTotal;
    }

    /// <summary>Every unpaid instalment due today or earlier, whatever its size: from the widest start the snapshot is loaded for through today (Dubai). Nothing due later is listed.</summary>
    public static readonly DateOnly WindowFrom = new(2000, 1, 1);

    public static readonly IReadOnlyList<(string Value, string Label)> StatusOptions =
        [("all", "All statuses"), ("overdue", "Overdue"), ("due", "Due")];

    public int? TowerId { get; private set; }
    /// <summary>Month and year of the instalments' DUE date; both empty = All months (everything due today or earlier).</summary>
    public int? Month { get; private set; }
    public int? Year { get; private set; }
    public decimal? MinTotal { get; private set; } = DefaultMinTotal;
    public string MinTotalText => MinTotal?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
    /// <summary>The chosen month starts after today: nothing is due in it yet (no data call is made).</summary>
    public bool NothingDueYet { get; private set; }
    public IEnumerable<int> YearOptions()
    {
        var current = CollectionsDisplay.DubaiToday().Year;
        return Enumerable.Range(2020, Math.Max(current, Year ?? 0) - 2020 + 1);
    }
    /// <summary><c>all</c>, <c>overdue</c> or <c>due</c>: the units that have an instalment with that status.</summary>
    public string Status { get; private set; } = "all";
    public string? Search { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public IReadOnlyList<CollectionsTowerDto> Towers { get; private set; } = [];
    public bool TowersFailed { get; private set; }
    public ApiOutcome Outcome { get; private set; }
    public string? Error { get; private set; }
    public string? LoadNotice { get; private set; }
    public PactInstalmentsPageDto? Report { get; private set; }
    public bool RenderFull { get; private set; }
    public int UnitCount => Report?.Totals.UnitCount ?? 0;
    public int TotalPages => Report is null ? 1 : Math.Max(1, (int)Math.Ceiling(UnitCount / (double)Report.PageSize));

    public static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Take(12).ToArray();

    public async Task OnGetAsync(int? towerId, string? status, string? search, int? month = null, int? year = null, int page = 1, string? render = null, string? load = null, CancellationToken cancellationToken = default)
    {
        Bind(towerId, status, search, month, year, page, load);
        RenderFull = render == "full";
        // The shell never waits for data. The tower list is a small local read; results arrive through the Results handler (or render=full without JavaScript).
        var towers = await api.GetTowersAsync(cancellationToken);
        Towers = towers.IsSuccess && towers.Value is { } list ? list : [];
        TowersFailed = !towers.IsSuccess;
        if (RenderFull) await LoadReportAsync(cancellationToken);
    }

    /// <summary>The results area only (HTML fragment).</summary>
    public async Task<IActionResult> OnGetResultsAsync(int? towerId, string? status, string? search, int? month = null, int? year = null, int page = 1, string? load = null, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        Bind(towerId, status, search, month, year, page, load);
        await LoadReportAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        var t = Report?.Timings;
        Response.Headers["Server-Timing"] = string.Create(CultureInfo.InvariantCulture,
            $"web;dur={timer.Elapsed.TotalMilliseconds:F0}, sql;dur={t?.SourceMs ?? 0:F0}, map;dur={t?.MapMs ?? 0:F0}");
        return Partial("_InstalmentResults", this);
    }

    private void Bind(int? towerId, string? status, string? search, int? month, int? year, int page, string? load)
    {
        TowerId = towerId;
        Status = StatusOptions.Any(o => o.Value == status?.Trim().ToLowerInvariant()) ? status!.Trim().ToLowerInvariant() : "all";
        Search = search?.Trim();
        MinTotal = ParseMinTotal(Request.Query);
        // A month needs its year (the current year when only the month was chosen); a year alone means All months.
        var today = CollectionsDisplay.DubaiToday();
        if (month is >= 1 and <= 12) { Month = month; Year = year is >= 2000 and <= 2100 ? year : today.Year; }
        PageNumber = Math.Max(1, page);
        LoadNotice = CollectionsDisplay.NoticeText(load) is { Length: > 0 } text ? text : null;
    }

    private async Task LoadReportAsync(CancellationToken cancellationToken)
    {
        // The dates are the instalments' DUE dates: the chosen month, or everything up to today. Never beyond today (nothing future is listed), and the
        // Due / Overdue classification stays against today whatever month is chosen.
        var today = CollectionsDisplay.DubaiToday();
        var (from, to) = (WindowFrom, today);
        if (Month is { } m && Year is { } y)
        {
            var (first, last) = TigerCS.Domain.Modules.Collections.CollectionsDateRanges.Month(y, m);
            if (first > today) { NothingDueYet = true; return; }
            (from, to) = (first, last < today ? last : today);
        }
        var result = await api.GetInstalmentsAsync(TowerId, from, to, "outstanding", null, Search, PageNumber, cancellationToken, "units", null, Status == "all" ? null : Status, MinTotal);
        Outcome = result.Outcome;
        Error = result.Detail;
        Report = result.IsSuccess ? result.Value : null;
    }

    /// <summary>Query string of the current filters (page 1 unless given); the same names the form submits.</summary>
    public string Query(int? page = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (TowerId is { } tower) query["towerId"] = tower.ToString(CultureInfo.InvariantCulture);
        if (Status != "all") query["status"] = Status;
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
        if (Month is { } m && Year is { } y) { query["month"] = m.ToString(CultureInfo.InvariantCulture); query["year"] = y.ToString(CultureInfo.InvariantCulture); }
        query["minTotal"] = MinTotalText;   // always sent: empty means "cleared" and must survive paging
        if (page is > 1) query["page"] = page.Value.ToString(CultureInfo.InvariantCulture);
        return query.ToString()!;
    }

    public string PageUrl(int page) => $"/Collections/Receivables?{Query(page)}&render=full";

    /// <summary>"1 unit" / "25 units".</summary>
    public static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count:N0} {many}";

    /// <summary>Starts the background load (no-JavaScript fallback; receivables.js posts the same form with fetch). With a company: only that company is retried.</summary>
    public async Task<IActionResult> OnPostLoadCoverageAsync(DateOnly dateFrom, DateOnly dateTo, string? returnUrl, int? companyId = null, CancellationToken cancellationToken = default)
    {
        var result = await api.RequestCoverageLoadAsync(dateFrom, dateTo, cancellationToken, companyId);
        var target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/Collections/Receivables";
        return LocalRedirect(CollectionsDisplay.WithNotice(target, CollectionsDisplay.NoticeCode(result)));
    }

    // ---- One unit row and its details. Every number on the row is derived from the very instalments the details list, so the two can never disagree. ----

    public const string Overdue = "Overdue", Due = "Due";

    /// <summary>Overdue: due before today; Due: due today (today = the Dubai date of the report). Instalments due later are not part of this page.</summary>
    public static string Classify(DateOnly dueDate, DateOnly today) => dueDate < today ? Overdue : Due;

    /// <summary>Whole days between the due date and today for an overdue instalment; null otherwise.</summary>
    public static int? DaysLate(DateOnly dueDate, DateOnly today) => dueDate < today ? today.DayNumber - dueDate.DayNumber : null;

    public sealed record InstalmentLine(int Number, string Description, DateOnly DueDate, decimal? Original, decimal? Paid, decimal Remaining, string Status, int? DaysLate);

    /// <summary><see cref="Total"/> = Due + Overdue = the sum of the listed instalments.
public sealed record UnitLine(string Customer, string Phone, string Email, string Tower, string Apartment, decimal Total, decimal Due, decimal Overdue,
        IReadOnlyList<InstalmentLine> Instalments, string DetailsId);

    public static IReadOnlyList<UnitLine> Lines(PactInstalmentsPageDto report)
    {
        var today = report.BusinessDate;
        var lines = new List<UnitLine>();
        foreach (var unit in report.Units ?? [])
        {
            // Oldest first; a remaining balance is what makes an instalment unpaid.
            var rows = unit.Instalments.Where(i => i.RemainingAmount > 0 && i.DueDate <= today).OrderBy(i => i.DueDate).ThenBy(i => i.VoucherNumber, StringComparer.Ordinal).ToList();
            var instalments = rows.Select((i, index) => new InstalmentLine(index + 1,
                i.VoucherNumber.Length > 0 ? $"Voucher {i.VoucherNumber}" : $"Instalment {index + 1}", i.DueDate, i.OriginalAmount, i.PaidAmount, i.RemainingAmount,
                Classify(i.DueDate, today), DaysLate(i.DueDate, today))).ToList();
            var tower = !string.IsNullOrWhiteSpace(unit.TowerName) ? unit.TowerName : unit.TowerNumber ?? "—";
            if (instalments.Count == 0) continue;   // nothing Due or Overdue: the unit is not listed
            lines.Add(new UnitLine(unit.CustomerName,
                First(rows.Select(r => r.Mobile)), First(rows.Select(r => r.Email)), tower, unit.UnitCode,
                instalments.Sum(i => i.Remaining), instalments.Where(i => i.Status == Due).Sum(i => i.Remaining), instalments.Where(i => i.Status == Overdue).Sum(i => i.Remaining),
                instalments, $"unit-{unit.CompanyId}-{unit.UnitId}-{Math.Abs(string.GetHashCode(unit.TenantId + "|" + unit.UnitCode, StringComparison.Ordinal))}"));
        }
        return lines;
    }

    private static string First(IEnumerable<string> values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "—";

    public static string Money(decimal? amount) => amount is { } value ? value.ToString("N2", CultureInfo.InvariantCulture) : "—";

    /// <summary>A zero amount in the Due / Overdue columns reads as a dash.</summary>
    public static string MoneyOrDash(decimal amount) => amount > 0 ? Money(amount) : "—";
}
