using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Authorization;
using TigerCS.Web.Infrastructure;
using TigerCS.Web.Services;

namespace TigerCS.Web.Pages.Tickets;

/// <summary>
/// The ticket queue (<c>GET /api/tickets</c>, MVP-API-Contracts.md §3.2).
///
/// <para>
/// <b>Visibility is the server's decision.</b> The department filter below
/// narrows what the caller may already see and can never widen it -
/// <c>TicketQueryAppService</c> resolves the visible department set from the
/// caller's own roles and assignments and applies it regardless of what this
/// page sends. Nothing here attempts to compensate for, pre-empt or work
/// around that.
/// </para>
/// </summary>
public sealed class IndexModel(
    TicketsApiClient ticketsApiClient,
    QueueSlaResolver slaResolver,
    DirectoryService directoryService,
    UserContextAccessor userContextAccessor) : PageModel
{
    private const int DefaultPageSize = 25;
    private static readonly int[] AllowedPageSizes = [25, 50, 100];

    // ---- Filter state, all bound from the query string so the queue is
    // ---- shareable, bookmarkable, and restorable when returning from a ticket.
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public int? DepartmentId { get; set; }
    [BindProperty(SupportsGet = true)] public int? CategoryId { get; set; }
    [BindProperty(SupportsGet = true)] public byte? PriorityId { get; set; }
    [BindProperty(SupportsGet = true)] public string? TicketStatus { get; set; }
    [BindProperty(SupportsGet = true)] public string? VerificationStatus { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? OwnerEmployeeId { get; set; }
    [BindProperty(SupportsGet = true)] public string? SortBy { get; set; }
    [BindProperty(SupportsGet = true)] public string? SortDir { get; set; }
    /// <summary>
    /// Bound from <c>?page=</c>. Named <c>PageNumber</c> rather than
    /// <c>Page</c> because <c>PageModel.Page()</c> already owns that name.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = DefaultPageSize;

    public UserContext CurrentUser { get; private set; } = UserContext.Anonymous;

    public IReadOnlyList<TicketSummaryDto> Tickets { get; private set; } = [];

    public int TotalCount { get; private set; }

    public IReadOnlyDictionary<long, TicketSlaSummaryResponseDto> SlaByTicketId { get; private set; } =
        new Dictionary<long, TicketSlaSummaryResponseDto>();

    public IReadOnlyDictionary<Guid, string> OwnerNames { get; private set; } = new Dictionary<Guid, string>();

    public ApiProblem? LoadProblem { get; private set; }

    public bool SlaBadgesEnabled => slaResolver.IsEnabled;

    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasAnyFilter =>
        !string.IsNullOrWhiteSpace(Search)
        || DepartmentId.HasValue || CategoryId.HasValue || PriorityId.HasValue
        || !string.IsNullOrWhiteSpace(TicketStatus)
        || !string.IsNullOrWhiteSpace(VerificationStatus)
        || OwnerEmployeeId.HasValue;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        CurrentUser = userContextAccessor.Current;

        PageNumber = Math.Max(PageNumber, 1);
        PageSize = AllowedPageSizes.Contains(PageSize) ? PageSize : DefaultPageSize;
        SortDir = string.Equals(SortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";

        var request = new TicketListRequestDto(
            DepartmentId,
            CategoryId,
            PriorityId,
            NullIfBlank(TicketStatus),
            NullIfBlank(VerificationStatus),
            OwnerEmployeeId,
            NullIfBlank(Search),
            NullIfBlank(SortBy),
            SortDir,
            PageNumber,
            PageSize);

        var result = await ticketsApiClient.GetQueueAsync(request, cancellationToken);

        if (!result.IsSuccess)
        {
            // A 401 here means the token behind the cookie is no longer
            // accepted. Sending the user to sign in again is the only useful
            // response; anything else leaves them staring at an error they
            // cannot act on.
            if (result.Problem!.StatusCode == HttpStatusCode.Unauthorized)
            {
                return RedirectToPage("/Account/Login", new { reason = "expired", returnUrl = CurrentUrl() });
            }

            LoadProblem = result.Problem;
            return Page();
        }

        var queue = result.Required;
        Tickets = queue.Items;
        TotalCount = queue.TotalCount;

        if (Tickets.Count > 0)
        {
            // Both lookups are page-scoped and independent, so they overlap
            // rather than queueing behind one another.
            var slaTask = slaResolver.ResolveAsync(
                Tickets.Select(t => t.TicketId).ToArray(), cancellationToken);

            var namesTask = directoryService.GetEmployeeNamesAsync(
                Tickets.Where(t => t.CurrentOwnerEmployeeId.HasValue)
                    .Select(t => t.CurrentDepartmentId),
                cancellationToken);

            await Task.WhenAll(slaTask, namesTask);

            SlaByTicketId = await slaTask;
            OwnerNames = await namesTask;
        }

        return Page();
    }

    /// <summary>The owner's display name, or null when it could not be resolved.</summary>
    public string? OwnerName(Guid? employeeId) =>
        employeeId.HasValue && OwnerNames.TryGetValue(employeeId.Value, out var name) ? name : null;

    /// <summary>
    /// The current query string, so a page can be returned to exactly as it
    /// was. This is what preserves filter state across a visit to a ticket.
    /// </summary>
    public string CurrentUrl() =>
        Request.QueryString.HasValue ? $"{Request.Path}{Request.QueryString}" : Request.Path.ToString();

    /// <summary>Route values for a link that keeps every filter and changes only what is named.</summary>
    public Dictionary<string, string?> RouteWith(
        string? sortBy = null, string? sortDir = null, int? page = null, int? pageSize = null)
    {
        var route = new Dictionary<string, string?>();

        AddIf(route, "search", Search);
        AddIf(route, "departmentId", DepartmentId?.ToString(CultureInfo.InvariantCulture));
        AddIf(route, "categoryId", CategoryId?.ToString(CultureInfo.InvariantCulture));
        AddIf(route, "priorityId", PriorityId?.ToString(CultureInfo.InvariantCulture));
        AddIf(route, "ticketStatus", TicketStatus);
        AddIf(route, "verificationStatus", VerificationStatus);
        AddIf(route, "ownerEmployeeId", OwnerEmployeeId?.ToString());
        AddIf(route, "sortBy", sortBy ?? SortBy);
        AddIf(route, "sortDir", sortDir ?? SortDir);
        AddIf(route, "pageSize", (pageSize ?? PageSize).ToString(CultureInfo.InvariantCulture));
        AddIf(route, "page", (page ?? PageNumber).ToString(CultureInfo.InvariantCulture));

        return route;
    }

    /// <summary>Route values for a filter chip that also resets to the first page.</summary>
    public Dictionary<string, string?> RouteWithFilter(string key, string? value)
    {
        var route = RouteWith(page: 1);

        if (string.IsNullOrEmpty(value))
        {
            route.Remove(key);
        }
        else
        {
            route[key] = value;
        }

        route["page"] = "1";
        return route;
    }

    /// <summary>The sort direction a column header link should request: flip if already sorted by it.</summary>
    public string NextSortDir(string column) =>
        string.Equals(SortBy, column, StringComparison.OrdinalIgnoreCase)
        && string.Equals(SortDir, "desc", StringComparison.OrdinalIgnoreCase)
            ? "asc"
            : "desc";

    /// <summary>The <c>aria-sort</c> value for a column header.</summary>
    public string AriaSort(string column) =>
        !string.Equals(SortBy, column, StringComparison.OrdinalIgnoreCase)
            ? "none"
            : string.Equals(SortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "ascending" : "descending";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void AddIf(IDictionary<string, string?> route, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            route[key] = value;
        }
    }
}
