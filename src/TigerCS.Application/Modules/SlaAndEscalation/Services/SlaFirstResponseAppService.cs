using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.SlaAndEscalation.Services;

/// <summary>
/// MVP-API-Contracts.md §5.2 — records the First Human Response, the only
/// event that satisfies the First Response SLA (ISSUE-019: the automated
/// acknowledgement never does, on any channel).
///
/// <para>
/// Recording the response immediately finalizes
/// <c>TicketSlaInstances.FirstResponseBreached</c> if it landed after the
/// deadline (§5.2's own "Domain events" note), through the same
/// <see cref="SlaBreachProcessor"/> and the same idempotency key the
/// scheduled job and the sweep use — so a response arriving moments after a
/// job already flagged the breach records nothing twice.
/// </para>
/// </summary>
public sealed class SlaFirstResponseAppService(
    ITicketRepository ticketRepository,
    Abstractions.ITicketSlaInstanceRepository slaInstanceRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    FirstHumanResponseRecorder firstHumanResponseRecorder,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<SlaOperationResult<TicketSlaSummaryResponseDto>> RecordAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        RecordFirstResponseRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.NotFound);
        }

        if (!Enum.TryParse<FirstResponseSource>(request.Source, ignoreCase: true, out var source))
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.InvalidRequest);
        }

        if (!await IsAuthorizedAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.Forbidden);
        }

        // Closed-ticket immutability, rejected before any transaction or
        // write — the same ordering TicketLifecycleAppService uses.
        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.TicketClosed);
        }

        if (ticket.FirstHumanResponseAtUtc is not null)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.FirstResponseAlreadyRecorded);
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var occurredAtUtc = request.OccurredAtUtc ?? nowUtc;
        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // The history row, the audit entry and the breach finalization all
        // live in FirstHumanResponseRecorder, which the Genesys transcript
        // path shares — ISSUE-019's measurement is written in exactly one way.
        // The already-recorded and Closed cases were rejected above with their
        // own outcomes, so a false here is that same race answered again.
        if (!await firstHumanResponseRecorder.TryRecordAsync(
                ticket, occurredAtUtc, nowUtc, callerEmployeeId, actorIsSystem: false,
                source, correlationId, cancellationToken))
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.FirstResponseAlreadyRecorded);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.ConcurrencyConflict);
        }
        catch (DuplicateWriteException)
        {
            // A concurrent detection path claimed the same breach key first.
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);

        return SlaOperationResult<TicketSlaSummaryResponseDto>.Success(
            SlaQueryAppService.ToSummaryDto(ticket, await slaInstanceRepository.GetCurrentAsync(ticketId, cancellationToken)));
    }

    /// <summary>MVP-API-Contracts.md §5.2's "Agent and above": the ticket's current owner, a cross-department supervisory role, or a Department Head in the ticket's own department. A permission rule, so it runs under the ADR-0024 override via the gate.</summary>
    private Task<bool> IsAuthorizedAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken) =>
        AuthorizationGate.EvaluateAsync(callerRoles, async () =>
        {
            if (ticket.CurrentOwnerEmployeeId == callerEmployeeId)
            {
                return true;
            }

            if (callerRoles.Any(SlaRoleSets.RecordFirstResponse.Contains))
            {
                return true;
            }

            return callerRoles.Contains(Roles.DepartmentHead)
                && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken);
        });
}

/// <summary>MVP-API-Contracts.md §5.2's <c>Source</c> enum. <see cref="GenesysCallAnswer"/> is part of the approved DTO; no Genesys adapter ships in this increment, so nothing populates it automatically yet.</summary>
public enum FirstResponseSource : byte
{
    Manual = 1,
    GenesysCallAnswer = 2,

    /// <summary>
    /// The first genuinely human message of a conversation was stored — an
    /// <see cref="Domain.Modules.Ticketing.InteractionMessageSender.HumanAgent"/>
    /// transcript line. Observed by the system rather than recorded by a
    /// person, and deliberately never reached by a
    /// <see cref="Domain.Modules.Ticketing.InteractionMessageSender.VirtualAgent"/>
    /// line: an AI reply is not a first human response (ISSUE-019).
    /// </summary>
    HumanAgentMessage = 3
}
