using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// This increment's item 6 — reconciles a provisional (Critical/High,
/// ISSUE-006) ticket once the CRM is reachable again. No merged API
/// contract predates provisional tickets, so this endpoint's shape is
/// designed in this increment; <see cref="Ticket.ReconcileVerification"/>'s
/// own remarks explicitly anticipated this ("the calling application
/// service does both [snapshot + status change] in the same transaction —
/// see TicketCreationAppService.CreateFromVerificationSessionAsync for the
/// pattern this method's future caller must mirror"). This service mirrors
/// that pattern, plus a safety check that pattern didn't need: confirming
/// the newly-confirmed session actually corresponds to the same real-world
/// interaction the provisional ticket was raised from, not a different
/// unit the reconciling agent happened to have open.
/// </summary>
public sealed class TicketReconciliationAppService(
    ITicketRepository ticketRepository,
    IIntakeRecordRepository intakeRecordRepository,
    IVerificationSessionRepository verificationSessionRepository,
    ITicketRequesterSnapshotRepository snapshotRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    SlaDueDateService slaDueDateService,
    TimeProvider timeProvider)
{
    public async Task<TicketMutationResult> ReconcileAsync(
        Guid callerEmployeeId,
        long ticketId,
        ReconcileTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        if (ticket.VerificationStatus != CrmVerificationStatus.PendingCrmVerification)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotPendingCrmVerification);
        }

        var session = await verificationSessionRepository.GetByIdAsync(request.VerificationSessionId, cancellationToken);
        if (session is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.VerificationSessionNotFound);
        }

        if (!session.IsOwnedBy(callerEmployeeId))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.VerificationSessionForbidden);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var sessionOutcome = session.EffectiveStatus(now) switch
        {
            VerificationSessionStatus.Expired => TicketMutationOutcome.VerificationSessionExpired,
            VerificationSessionStatus.Consumed => TicketMutationOutcome.VerificationSessionAlreadyConsumed,
            VerificationSessionStatus.Confirmed => (TicketMutationOutcome?)null,
            _ => TicketMutationOutcome.VerificationSessionNotConfirmed
        };
        if (sessionOutcome is { } failureOutcome)
        {
            return TicketMutationResult.Failure(failureOutcome);
        }

        // Safety guard (item 6): confirm the session's raw unit context
        // actually matches the interaction this ticket was raised from —
        // reconciliation must never attach the wrong CRM unit/contact to a
        // provisional ticket just because the reconciling agent happened to
        // have a different, unrelated confirmed session open.
        var originatingIntakeRecord = await intakeRecordRepository.GetByLinkedTicketIdAsync(ticketId, cancellationToken);
        if (originatingIntakeRecord is null || !RawUnitContextMatches(originatingIntakeRecord.RawUnitNumberEntered, session.SnapshotUnitNumber))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ReconciliationUnitMismatch);
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            ticket.ReconcileVerification(session.UnitReferenceId, session.ContactReferenceId);
        }
        catch (TicketNotPendingCrmVerificationException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotPendingCrmVerification);
        }

        session.Consume(ticketId, now);

        await snapshotRepository.AddAsync(
            new TicketRequesterSnapshot(
                ticketId,
                session.SnapshotUnitNumber ?? string.Empty,
                session.SnapshotPropertyName,
                session.SnapshotTowerName,
                session.SnapshotUnitType,
                session.SnapshotContactDisplayName,
                session.SnapshotContactChannel,
                now),
            cancellationToken);

        var correlationId = Guid.NewGuid();
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.VerificationStatus,
                (byte)CrmVerificationStatus.PendingCrmVerification, (byte)CrmVerificationStatus.Verified,
                callerEmployeeId, actorIsSystem: false, note: null, correlationId, now),
            cancellationToken);

        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.SlaState,
                (byte)SlaState.Paused, (byte)ticket.SlaState,
                callerEmployeeId, actorIsSystem: false,
                note: "SLA clock started at CRM reconciliation (FR-TKT-09).", correlationId, now),
            cancellationToken);

        // The SLA clock starts here, not at creation, for this one path.
        // FR-TKT-09 keeps an unverified ticket unclocked, so a provisional
        // ticket has carried no TicketSlaInstance until now; `now` is the
        // moment it became Verified and is therefore its clock-start event.
        // See Ticket.ReconcileVerification's remarks for how this reconciles
        // with MVP-ERD.md §2.15's "Required (≥1 from creation)".
        await slaDueDateService.OpenInitialPeriodAsync(ticket, now, callerEmployeeId, correlationId, cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Reconcile", "Ticket", ticketId.ToString(),
            beforeValue: "VerificationStatus=PendingCrmVerification", afterValue: "VerificationStatus=Verified",
            correlationId, cancellationToken);
        await auditWriter.WriteAsync(
            callerEmployeeId, "ConsumeVerificationSession", "VerificationSession", session.VerificationSessionId.ToString(),
            beforeValue: null, afterValue: $"ConsumedByTicketId={ticketId}", correlationId, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (VerificationSessionConcurrentlyConsumedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.VerificationSessionAlreadyConsumed);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }
        catch (DuplicateWriteException)
        {
            // Reachable only since this method began opening the ticket's SLA
            // period: two concurrent reconciliations both past the
            // PendingCrmVerification check would race the one-current-period
            // -per-ticket index. The loser rolls back whole.
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    /// <summary>
    /// Normalized (trim + case-insensitive) comparison between the raw,
    /// as-spoken unit number captured at intake and the raw unit number
    /// captured again at session confirmation. This exact matching
    /// algorithm is this increment's own design (no merged document
    /// specifies one) — a defensive minimum, not a fuzzy/typo-tolerant
    /// match, so a genuine mismatch is never silently waved through.
    /// </summary>
    private static bool RawUnitContextMatches(string? rawUnitNumberEntered, string? sessionSnapshotUnitNumber) =>
        !string.IsNullOrWhiteSpace(rawUnitNumberEntered)
        && !string.IsNullOrWhiteSpace(sessionSnapshotUnitNumber)
        && string.Equals(rawUnitNumberEntered.Trim(), sessionSnapshotUnitNumber.Trim(), StringComparison.OrdinalIgnoreCase);
}
