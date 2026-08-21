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
    ITicketStatusHistoryRepository statusHistoryRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    SlaBreachProcessor breachProcessor,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
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

        try
        {
            ticket.RecordFirstHumanResponse(occurredAtUtc);
        }
        catch (TicketClosedException)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.TicketClosed);
        }
        catch (FirstResponseAlreadyRecordedException)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.FirstResponseAlreadyRecorded);
        }

        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.SlaState, (byte)ticket.SlaState, (byte)ticket.SlaState,
                callerEmployeeId, actorIsSystem: false,
                note: $"First human response recorded ({source}) at {occurredAtUtc:O}.", correlationId, nowUtc),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "RecordFirstResponse", nameof(Ticket), ticketId.ToString(),
            beforeValue: "{\"firstHumanResponseAtUtc\":null}",
            afterValue: $"{{\"firstHumanResponseAtUtc\":\"{occurredAtUtc:O}\",\"source\":\"{source}\"}}",
            correlationId, cancellationToken);

        // Finalizes the breach flag if the response landed late — and, if it
        // did, raises the automatic Level 2 escalation exactly as a scheduled
        // job would have.
        await breachProcessor.ProcessDeadlineAsync(
            ticket, SlaDeadlineType.FirstResponse, nowUtc, correlationId, cancellationToken);

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
    GenesysCallAnswer = 2
}
