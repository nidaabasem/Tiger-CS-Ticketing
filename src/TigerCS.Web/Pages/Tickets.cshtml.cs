using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Models;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

public sealed record TicketQueueRow(TicketSummaryDto Ticket, string? DepartmentName, string? OwnerName, TicketSlaSummaryResponseDto? Sla);

public sealed class TicketsModel(
    TicketsApiClient ticketsApiClient,
    TicketSlaApiClient slaApiClient,
    TicketNameResolver nameResolver,
    ChannelsApiClient? channelsApiClient = null,
    RequestTypesApiClient? requestTypesApiClient = null) : PageModel
{
    // ---- bound filter state (query string) ----
    public int? DepartmentId { get; set; }
    public byte? PriorityId { get; set; }
    public string? TicketStatus { get; set; }
    public string? VerificationStatus { get; set; }
    public Guid? OwnerEmployeeId { get; set; }
    public string? Search { get; set; }
    public string? SortBy { get; set; }
    public string? SortDir { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    // ---- Dashboard drill-down filters (Dashboard Phase 1) — bound from the
    // query string and passed straight through to the same queue endpoint;
    // the Api evaluates each one with the exact predicate the dashboard
    // counted with. ----
    public byte? ChannelId { get; set; }
    public int? RequestTypeId { get; set; }
    public bool ActiveOnly { get; set; }
    public bool InDepartmentQueue { get; set; }
    public bool SlaBreached { get; set; }
    public bool DueToday { get; set; }
    public string? BacklogAge { get; set; }
    public bool PendingApproval { get; set; }
    public DateOnly? CreatedFrom { get; set; }
    public DateOnly? CreatedTo { get; set; }

    /// <summary>True when any dashboard drill-down criterion is active — the page then shows what it is filtering by, with a way to clear it.</summary>
    public bool HasDrilldown =>
        ChannelId is not null || RequestTypeId is not null || ActiveOnly || InDepartmentQueue || SlaBreached
        || DueToday || BacklogAge is not null || PendingApproval || CreatedFrom is not null || CreatedTo is not null;

    /// <summary>The channel/request-type names behind the drill-down ids, resolved from the same directories the New Ticket wizard reads — never a raw id on screen.</summary>
    public string? ChannelName { get; private set; }
    public string? RequestTypeName { get; private set; }

    /// <summary>The human-readable drill-down criteria, for the "Filtered from Dashboard" line.</summary>
    public IReadOnlyList<string> DrilldownLabels
    {
        get
        {
            var labels = new List<string>();
            if (ActiveOnly) labels.Add("Open tickets");
            if (InDepartmentQueue) labels.Add("In Department Queue");
            if (SlaBreached) labels.Add("SLA breached");
            if (DueToday) labels.Add("Due today");
            if (PendingApproval) labels.Add("Pending my approval");
            if (BacklogAge is { } age) labels.Add($"Backlog age: {TicketDisplay.BacklogAgeLabel(age)}");
            if (ChannelId is not null) labels.Add(ChannelName is null ? "Channel filter" : $"Channel: {ChannelName}");
            if (RequestTypeId is not null) labels.Add(RequestTypeName is null ? "Request type filter" : $"Request type: {RequestTypeName}");
            if (CreatedFrom is not null || CreatedTo is not null)
            {
                labels.Add($"Created {CreatedFrom?.ToString("MMM d, yyyy") ?? "…"} – {CreatedTo?.ToString("MMM d, yyyy") ?? "…"}");
            }

            return labels;
        }
    }

    public ApiOutcome Outcome { get; private set; } = ApiOutcome.Success;

    public IReadOnlyList<TicketQueueRow> Rows { get; private set; } = [];

    public int TotalCount { get; private set; }

    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public TicketNameResolver NameResolver => nameResolver;

    public CurrentUser? Viewer { get; private set; }

    // Small, secondary stat counts — each a real, separately-filtered TotalCount from the same queue endpoint.
    public int? OpenCount { get; private set; }
    public int? InProgressCount { get; private set; }
    public int? PendingCustomerCount { get; private set; }
    public int? ClosedCount { get; private set; }

    public async Task OnGetAsync(
        int? departmentId, byte? priorityId, string? ticketStatus, string? verificationStatus,
        Guid? ownerEmployeeId, string? search, string? sortBy, string? sortDir, int page, int pageSize,
        CancellationToken cancellationToken,
        byte? channelId = null, int? requestTypeId = null, bool activeOnly = false, bool inDepartmentQueue = false,
        bool slaBreached = false, bool dueToday = false, string? backlogAge = null, bool pendingApproval = false,
        DateOnly? createdFrom = null, DateOnly? createdTo = null)
    {
        Viewer = CurrentUser.FromPrincipal(User);

        DepartmentId = departmentId;
        PriorityId = priorityId;
        TicketStatus = ticketStatus;
        VerificationStatus = verificationStatus;
        OwnerEmployeeId = ownerEmployeeId;
        Search = search;
        SortBy = sortBy;
        SortDir = sortDir;
        PageNumber = page < 1 ? 1 : page;
        PageSize = pageSize is < 1 or > 100 ? 20 : pageSize;
        ChannelId = channelId;
        RequestTypeId = requestTypeId;
        ActiveOnly = activeOnly;
        InDepartmentQueue = inDepartmentQueue;
        SlaBreached = slaBreached;
        DueToday = dueToday;
        BacklogAge = backlogAge;
        PendingApproval = pendingApproval;
        CreatedFrom = createdFrom;
        CreatedTo = createdTo;

        // Own memberships drive the "my departments" filter; the directory
        // puts a name on every row's department, member or not.
        await nameResolver.PrimeDepartmentsAsync(cancellationToken);

        var statsTask = LoadStatsAsync(cancellationToken);
        var drilldownNamesTask = LoadDrilldownNamesAsync(cancellationToken);

        var request = new TicketListRequestDto(
            DepartmentId, null, PriorityId, TicketStatus, VerificationStatus, OwnerEmployeeId,
            Search, SortBy, SortDir, PageNumber, PageSize,
            ChannelId, RequestTypeId,
            ActiveOnly ? true : null, InDepartmentQueue ? true : null, SlaBreached ? true : null, DueToday ? true : null,
            BacklogAge, PendingApproval ? true : null, CreatedFrom, CreatedTo);

        var result = await ticketsApiClient.GetQueueAsync(request, cancellationToken);
        Outcome = result.Outcome;

        if (result.IsSuccess && result.Value is not null)
        {
            TotalCount = result.Value.TotalCount;
            Rows = await BuildRowsAsync(result.Value.Items, cancellationToken);
        }

        await statsTask;
        await drilldownNamesTask;
    }

    /// <summary>Best-effort names for the channel/request-type drill-down chips; a failed directory call only degrades the chip's wording.</summary>
    private async Task LoadDrilldownNamesAsync(CancellationToken cancellationToken)
    {
        if (ChannelId is { } channelId && channelsApiClient is not null)
        {
            var channels = await channelsApiClient.GetChannelsAsync(activeOnly: false, cancellationToken);
            ChannelName = channels.IsSuccess ? channels.Value?.FirstOrDefault(c => c.ChannelId == channelId)?.Name : null;
        }

        if (RequestTypeId is { } requestTypeId && requestTypesApiClient is not null)
        {
            var requestTypes = await requestTypesApiClient.GetOptionsAsync(null, cancellationToken);
            RequestTypeName = requestTypes.IsSuccess ? requestTypes.Value?.FirstOrDefault(r => r.RequestTypeId == requestTypeId)?.Name : null;
        }
    }

    private async Task LoadStatsAsync(CancellationToken cancellationToken)
    {
        var open = ticketsApiClient.GetQueueAsync(new TicketListRequestDto(null, null, null, "Open", null, null, null, null, null, 1, 1), cancellationToken);
        var inProgress = ticketsApiClient.GetQueueAsync(new TicketListRequestDto(null, null, null, "InProgress", null, null, null, null, null, 1, 1), cancellationToken);
        var pendingCustomer = ticketsApiClient.GetQueueAsync(new TicketListRequestDto(null, null, null, "PendingCustomer", null, null, null, null, null, 1, 1), cancellationToken);
        var closed = ticketsApiClient.GetQueueAsync(new TicketListRequestDto(null, null, null, "Closed", null, null, null, null, null, 1, 1), cancellationToken);

        await Task.WhenAll(open, inProgress, pendingCustomer, closed);

        OpenCount = open.Result.IsSuccess ? open.Result.Value?.TotalCount : null;
        InProgressCount = inProgress.Result.IsSuccess ? inProgress.Result.Value?.TotalCount : null;
        PendingCustomerCount = pendingCustomer.Result.IsSuccess ? pendingCustomer.Result.Value?.TotalCount : null;
        ClosedCount = closed.Result.IsSuccess ? closed.Result.Value?.TotalCount : null;
    }

    private async Task<IReadOnlyList<TicketQueueRow>> BuildRowsAsync(IReadOnlyList<TicketSummaryDto> items, CancellationToken cancellationToken)
    {
        var slaTasks = items.Select(t => slaApiClient.GetSlaAsync(t.TicketId, cancellationToken)).ToArray();
        await Task.WhenAll(slaTasks);

        var rows = new List<TicketQueueRow>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var ticket = items[i];
            var departmentName = nameResolver.TryGetDepartmentName(ticket.CurrentDepartmentId);
            var ownerName = ticket.CurrentOwnerEmployeeId is Guid ownerId
                ? await nameResolver.ResolveOwnerNameAsync(ticket.CurrentDepartmentId, ownerId, cancellationToken)
                : null;
            var slaResult = slaTasks[i].Result;
            rows.Add(new TicketQueueRow(ticket, departmentName, ownerName, slaResult.IsSuccess ? slaResult.Value : null));
        }

        return rows;
    }
}
