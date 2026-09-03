using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The operational Dashboard read (Customer Workspace phase): KPI counts
/// and the Tickets Requiring Attention list, in one scoped repository
/// aggregate. Department visibility is resolved through the exact same
/// primitive as the ticket queue
/// (<see cref="TicketQueryAppService.ResolveVisibleDepartmentIdsAsync"/>) —
/// a department user's dashboard covers their own departments only, never
/// widened, and the caller never supplies the scope.
/// </summary>
public sealed class DashboardAppService(
    ITicketRepository ticketRepository,
    TicketQueryAppService ticketQueryAppService,
    TimeProvider timeProvider)
{
    /// <summary>How far ahead of a pending SLA deadline counts as "at risk" — a presentation threshold, not an SLA rule; the SLA clocks themselves are untouched by it.</summary>
    public static readonly TimeSpan AtRiskWindow = TimeSpan.FromHours(4);

    /// <summary>The attention list stays a short, scannable queue — it links to the full ticket queue for everything else.</summary>
    public const int AttentionLimit = 10;

    public async Task<DashboardSummaryDto> GetSummaryAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        CancellationToken cancellationToken = default)
    {
        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(
            callerEmployeeId, callerRoles, cancellationToken);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var snapshot = await ticketRepository.GetDashboardSnapshotAsync(
            new DashboardSnapshotQuery(
                visibleDepartmentIds,
                callerEmployeeId,
                nowUtc,
                // "Today" is the UTC calendar day — a deliberate, documented
                // simplification until a business-timezone day boundary is an
                // approved requirement.
                nowUtc.Date,
                AtRiskWindow,
                AttentionLimit),
            cancellationToken);

        return new DashboardSummaryDto(
            snapshot.OpenTickets,
            snapshot.Unassigned,
            snapshot.SlaAtRisk,
            snapshot.SlaBreached,
            snapshot.CriticalOrHigh,
            snapshot.PendingCustomer,
            snapshot.ResolvedToday,
            snapshot.Reopened,
            snapshot.MyTickets,
            snapshot.AttentionTickets.Select(ToAttentionDto).ToList());
    }

    private static DashboardAttentionTicketDto ToAttentionDto(DashboardAttentionTicket row) => new(
        row.Ticket.TicketId,
        row.Ticket.TicketNumber,
        row.Ticket.CrmBuyerCustomerName,
        row.Ticket.CrmBuyerUnitNumber ?? row.Ticket.ManualUnitNumber,
        row.Ticket.PriorityId,
        row.Ticket.TicketStatus.ToString(),
        row.Ticket.SlaState.ToString(),
        row.SlaDueAtUtc,
        row.Ticket.CurrentOwnerEmployeeId,
        row.Ticket.CurrentDepartmentId,
        row.Ticket.RequestSummary,
        row.Ticket.CreatedAtUtc);
}
