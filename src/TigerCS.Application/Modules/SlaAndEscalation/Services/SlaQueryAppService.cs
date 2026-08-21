using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.SlaAndEscalation.Services;

/// <summary>
/// Read-side of the SLA and escalation module (MVP-API-Contracts.md
/// §5.1/§5.9).
///
/// <para>
/// Department visibility is the same rule the ticket queue and detail
/// endpoints use, reached through <c>TicketQueryAppService</c> rather than
/// re-implemented: an SLA panel is ticket data, so a caller who cannot see
/// the ticket must not learn its SLA position or its escalation history
/// either (Security-Architecture.md §3).
/// </para>
/// </summary>
public sealed class SlaQueryAppService(
    ITicketRepository ticketRepository,
    ITicketSlaInstanceRepository slaInstanceRepository,
    ITicketEscalationRepository escalationRepository,
    TicketQueryAppService ticketQueryAppService)
{
    /// <summary>MVP-API-Contracts.md §5.1 — the ticket detail screen's SLA panel.</summary>
    public async Task<SlaOperationResult<TicketSlaSummaryResponseDto>> GetSummaryAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewDepartmentAsync(
                callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, cancellationToken))
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.Forbidden);
        }

        // Null for a provisional ticket still awaiting CRM reconciliation:
        // FR-TKT-09 keeps its clock unstarted, so there is no period and no
        // due timestamp to report. The response says so honestly rather than
        // fabricating a deadline.
        var instance = await slaInstanceRepository.GetCurrentAsync(ticketId, cancellationToken);

        return SlaOperationResult<TicketSlaSummaryResponseDto>.Success(ToSummaryDto(ticket, instance));
    }

    /// <summary>The §5.1 response shape, built once here so every endpoint that returns an SLA summary returns the identical projection.</summary>
    internal static TicketSlaSummaryResponseDto ToSummaryDto(Ticket ticket, TicketSlaInstance? instance) =>
        new(SlaState: ticket.SlaState.ToString(),
            FirstResponseDueAtUtc: instance?.FirstResponseDueAtUtc,
            FirstResponseBreached: instance?.FirstResponseBreached ?? false,
            FirstHumanResponseAtUtc: ticket.FirstHumanResponseAtUtc,
            ResolutionDueAtUtc: instance?.ResolutionDueAtUtc,
            ResolutionBreached: instance?.ResolutionBreached ?? false,

            // MVP-Implementation-Backlog.md §0.2 — SLA pause/resume is not
            // built in this pilot. The three fields stay in the approved §5.1
            // response shape at their pause-free values rather than being
            // dropped from the contract.
            IsCurrentlyPaused: false,
            CurrentPauseReason: null,
            TotalPausedMinutesThisPeriod: 0,

            EscalationLevel: ticket.EscalationLevel.ToString());

    /// <summary>MVP-API-Contracts.md §5.9 — escalation history for a ticket, most recent first.</summary>
    public async Task<SlaOperationResult<IReadOnlyList<TicketEscalationResponseDto>>> ListEscalationsAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return SlaOperationResult<IReadOnlyList<TicketEscalationResponseDto>>.Failure(SlaOperationOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewDepartmentAsync(
                callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, cancellationToken))
        {
            return SlaOperationResult<IReadOnlyList<TicketEscalationResponseDto>>.Failure(SlaOperationOutcome.Forbidden);
        }

        var escalations = await escalationRepository.ListByTicketIdAsync(ticketId, cancellationToken);

        return SlaOperationResult<IReadOnlyList<TicketEscalationResponseDto>>.Success(
            escalations.Select(ToDto).ToList());
    }

    internal static TicketEscalationResponseDto ToDto(TicketEscalation escalation) =>
        new(escalation.TicketEscalationId,
            escalation.TicketId,
            escalation.Level,
            escalation.TriggerType.ToString(),
            escalation.NotifiedRoles,
            escalation.RaisedAtUtc,
            escalation.RespondedAtUtc,
            escalation.RespondingEmployeeId);
}
