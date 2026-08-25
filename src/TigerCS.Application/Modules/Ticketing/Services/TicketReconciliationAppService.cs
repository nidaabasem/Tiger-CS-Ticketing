using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Links a confirmed CRM unit/contact match onto a ticket that did not have
/// one at creation — the "later enrichment" path for a ticket that started
/// <see cref="CrmVerificationStatus.Unverified"/> (business-rule change:
/// customer lookup no longer gates creation, so an agent who created a
/// ticket without a match — none found, a source was down, or they simply
/// didn't look — can attach one afterward once it becomes available, via a
/// confirmed <see cref="VerificationSession"/>). Includes a safety check:
/// confirming the newly-confirmed session actually corresponds to the same
/// real-world interaction the ticket was raised from, not a different unit
/// the reconciling agent happened to have open.
/// </summary>
public sealed class TicketReconciliationAppService(
    ITicketRepository ticketRepository,
    IIntakeRecordRepository intakeRecordRepository,
    IVerificationSessionRepository verificationSessionRepository,
    ITicketRequesterSnapshotRepository snapshotRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
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

        if (ticket.VerificationStatus == CrmVerificationStatus.Verified)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.AlreadyVerified);
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

        var previousVerificationStatus = ticket.VerificationStatus;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            ticket.ReconcileVerification(session.UnitReferenceId, session.ContactReferenceId);
        }
        catch (TicketAlreadyVerifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.AlreadyVerified);
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
                (byte)previousVerificationStatus, (byte)CrmVerificationStatus.Verified,
                callerEmployeeId, actorIsSystem: false, note: null, correlationId, now),
            cancellationToken);

        // No SlaState change or new TicketSlaInstance here — business-rule
        // change: every ticket's SLA clock now starts at creation
        // (Ticket.CreateUnverified sets SlaState.Running immediately, and
        // TicketCreationAppService always opens the initial period), so a
        // ticket being reconciled here already has one running. Reconciling
        // only ever links the unit/contact and flips VerificationStatus.
        await auditWriter.WriteAsync(
            callerEmployeeId, "Reconcile", "Ticket", ticketId.ToString(),
            beforeValue: $"VerificationStatus={previousVerificationStatus}", afterValue: "VerificationStatus=Verified",
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
