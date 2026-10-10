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
    /// <summary>Only used as the Campaigns default minimum amount; the Receivables page lists every unpaid instalment whatever its size.</summary>
    public const decimal DefaultMinAmount = 100m;

    /// <summary>Every unpaid instalment due today or earlier, whatever its size: from the widest start the snapshot is loaded for through today (Dubai). Nothing due later is listed.</summary>
    public static readonly DateOnly WindowFrom = new(2000, 1, 1);

    public static readonly IReadOnlyList<(string Value, string Label)> StatusOptions =
        [("all", "All statuses"), ("overdue", "Overdue"), ("due", "Due")];

    public int? TowerId { get; private set; }
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

    public async Task OnGetAsync(int? towerId, string? status, string? search, int page = 1, string? render = null, string? load = null, CancellationToken cancellationToken = default)
    {
        Bind(towerId, status, search, page, load);
        RenderFull = render == "full";
        // The shell never waits for data. The tower list is a small local read; results arrive through the Results handler (or render=full without JavaScript).
        var towers = await api.GetTowersAsync(cancellationToken);
        Towers = towers.IsSuccess && towers.Value is { } list ? list : [];
        TowersFailed = !towers.IsSuccess;
        if (RenderFull) await LoadReportAsync(cancellationToken);
    }

    /// <summary>The results area only (HTML fragment).</summary>
    public async Task<IActionResult> OnGetResultsAsync(int? towerId, string? status, string? search, int page = 1, string? load = null, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        Bind(towerId, status, search, page, load);
        await LoadReportAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        var t = Report?.Timings;
        Response.Headers["Server-Timing"] = string.Create(CultureInfo.InvariantCulture,
            $"web;dur={timer.Elapsed.TotalMilliseconds:F0}, sql;dur={t?.SourceMs ?? 0:F0}, map;dur={t?.MapMs ?? 0:F0}");
        return Partial("_InstalmentResults", this);
    }

    private void Bind(int? towerId, string? status, string? search, int page, string? load)
    {
        TowerId = towerId;
        Status = StatusOptions.Any(o => o.Value == status?.Trim().ToLowerInvariant()) ? status!.Trim().ToLowerInvariant() : "all";
        Search = search?.Trim();
        PageNumber = Math.Max(1, page);
        LoadNotice = CollectionsDisplay.NoticeText(load) is { Length: > 0 } text ? text : null;
    }

    private async Task LoadReportAsync(CancellationToken cancellationToken)
    {
        var result = await api.GetInstalmentsAsync(TowerId, WindowFrom, CollectionsDisplay.DubaiToday(), "outstanding", 0m, Search, PageNumber, cancellationToken, "units", null, Status == "all" ? null : Status);
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
        if (page is > 1) query["page"] = page.Value.ToString(CultureInfo.InvariantCulture);
        return query.ToString()!;
    }

    public string PageUrl(int page) => Query(page) is { Length: > 0 } query ? $"/Collections/Receivables?{query}&render=full" : "/Collections/Receivables?render=full";

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
            var tower = unit.TowerNumber is null ? "—" : string.IsNullOrWhiteSpace(unit.TowerName) ? unit.TowerNumber : $"{unit.TowerNumber} - {unit.TowerName}";
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
