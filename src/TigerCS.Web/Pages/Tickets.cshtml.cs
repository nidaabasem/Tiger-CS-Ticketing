using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

public sealed record TicketQueueRow(TicketSummaryDto Ticket, string? DepartmentName, string? OwnerName, TicketSlaSummaryResponseDto? Sla);

public sealed class TicketsModel(TicketsApiClient ticketsApiClient, TicketSlaApiClient slaApiClient, TicketNameResolver nameResolver) : PageModel
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
        CancellationToken cancellationToken)
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

        await nameResolver.PrimeOwnDepartmentsAsync(cancellationToken);

        var statsTask = LoadStatsAsync(cancellationToken);

        var request = new TicketListRequestDto(
            DepartmentId, null, PriorityId, TicketStatus, VerificationStatus, OwnerEmployeeId,
            Search, SortBy, SortDir, PageNumber, PageSize);

        var result = await ticketsApiClient.GetQueueAsync(request, cancellationToken);
        Outcome = result.Outcome;

        if (result.IsSuccess && result.Value is not null)
        {
            TotalCount = result.Value.TotalCount;
            Rows = await BuildRowsAsync(result.Value.Items, cancellationToken);
        }

        await statsTask;
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
