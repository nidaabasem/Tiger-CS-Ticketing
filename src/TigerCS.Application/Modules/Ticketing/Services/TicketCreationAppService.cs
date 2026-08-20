using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Items 3–8 of this increment's scope: create a ticket either (a) from an
/// already-confirmed <see cref="VerificationSession"/> for a unit-related
/// request (the normal path, FR-CH-01/FR-VER-02), or (b) as an approved
/// provisional ticket for a Critical/High request when the CRM is
/// unavailable (ISSUE-006, Management-Decisions.md — approved as
/// recommended). Medium/Low requests during an outage are never turned into
/// a ticket here — they remain queued on their IntakeRecord instead
/// (<see cref="TicketCreationOutcome.QueuedPendingVerification"/>).
///
/// <para>
/// <b>Two SaveChanges calls per path, not one.</b> Both <see cref="Ticket"/>
/// and <see cref="IntakeRecord"/> use database-generated identity PKs
/// (bigint), so a <see cref="Ticket"/>'s real <c>TicketId</c> is not known
/// until its own insert commits — but consuming the session, linking the
/// IntakeRecord, and writing the requester snapshot/status-history/audit
/// rows all need that real ID. The ticket is therefore inserted alone
/// first; everything else commits together in a second call. If the first
/// insert collides on the TicketNumber unique index (a rare same-department,
/// same-day race — MVP-Data-Dictionary.md §2.10), nothing else has been
/// touched yet, so the whole request is safe to retry unchanged
/// (<see cref="TicketCreationOutcome"/> does not have a dedicated case for
/// this — the underlying <c>DuplicateWriteException</c> propagates, since a
/// same-second double insert of the same prefix is exceedingly unlikely at
/// this pilot's scale and a plain retry is always correct here).
/// </para>
/// </summary>
public sealed class TicketCreationAppService(
    IIntakeRecordRepository intakeRecordRepository,
    IVerificationSessionRepository verificationSessionRepository,
    ICategoryRepository categoryRepository,
    IPriorityRepository priorityRepository,
    IDepartmentRepository departmentRepository,
    ITicketRepository ticketRepository,
    ITicketRequesterSnapshotRepository snapshotRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<TicketCreationResult> CreateFromVerificationSessionAsync(
        Guid callerEmployeeId, CreateTicketFromVerificationRequestDto request, CancellationToken cancellationToken = default)
    {
        var intakeRecord = await intakeRecordRepository.GetByIdAsync(request.IntakeRecordId, cancellationToken);
        if (intakeRecord is null)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.IntakeRecordNotFound);
        }

        if (intakeRecord.LinkedTicketId is not null)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.IntakeRecordAlreadyLinked);
        }

        if (!intakeRecord.IsUnitRelated)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.IntakeRecordNotUnitRelated);
        }

        var session = await verificationSessionRepository.GetByIdAsync(request.VerificationSessionId, cancellationToken);
        if (session is null)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.VerificationSessionNotFound);
        }

        // Single-agent ownership (MVP-ERD.md §2.24) — same rule VerificationSessionsController enforces on read.
        if (!session.IsOwnedBy(callerEmployeeId))
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.VerificationSessionForbidden);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var effectiveStatus = session.EffectiveStatus(now);
        var sessionOutcome = effectiveStatus switch
        {
            VerificationSessionStatus.Expired => TicketCreationOutcome.VerificationSessionExpired,
            VerificationSessionStatus.Consumed => TicketCreationOutcome.VerificationSessionAlreadyConsumed,
            VerificationSessionStatus.Confirmed => (TicketCreationOutcome?)null,
            _ => TicketCreationOutcome.VerificationSessionNotConfirmed
        };
        if (sessionOutcome is { } failureOutcome)
        {
            return TicketCreationResult.Failure(failureOutcome);
        }

        var category = await categoryRepository.GetByIdAsync(request.CategoryId, cancellationToken);
        if (category is null || !category.IsActive)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.CategoryNotFound);
        }

        var priority = await priorityRepository.GetByIdAsync(request.PriorityId, cancellationToken);
        if (priority is null)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.PriorityNotFound);
        }

        var ticketNumber = await GenerateTicketNumberAsync(category.DepartmentId, now, cancellationToken);
        var ticket = Ticket.CreateVerified(
            ticketNumber, category.DepartmentId, session.UnitReferenceId, session.ContactReferenceId,
            category.CategoryId, priority.PriorityId, request.RequestSummary, now);

        await ticketRepository.AddAsync(ticket, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        session.Consume(ticket.TicketId, now);
        intakeRecord.LinkToTicket(ticket.TicketId, CrmVerificationStatus.Verified);

        await snapshotRepository.AddAsync(
            new TicketRequesterSnapshot(
                ticket.TicketId,
                session.SnapshotUnitNumber ?? string.Empty,
                session.SnapshotPropertyName,
                session.SnapshotTowerName,
                session.SnapshotUnitType,
                session.SnapshotContactDisplayName,
                session.SnapshotContactChannel,
                now),
            cancellationToken);

        await SeedStatusHistoryAsync(ticket, callerEmployeeId, now, cancellationToken);

        var correlationId = Guid.NewGuid();
        await auditWriter.WriteAsync(
            callerEmployeeId, "Create", "Ticket", ticket.TicketId.ToString(),
            beforeValue: null, afterValue: $"TicketNumber={ticket.TicketNumber};VerificationStatus=Verified",
            correlationId, cancellationToken);
        await auditWriter.WriteAsync(
            callerEmployeeId, "ConsumeVerificationSession", "VerificationSession", session.VerificationSessionId.ToString(),
            beforeValue: null, afterValue: $"ConsumedByTicketId={ticket.TicketId}",
            correlationId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return TicketCreationResult.Success(ToDto(ticket));
    }

    public async Task<TicketCreationResult> CreateProvisionalAsync(
        Guid callerEmployeeId, CreateProvisionalTicketRequestDto request, CancellationToken cancellationToken = default)
    {
        var intakeRecord = await intakeRecordRepository.GetByIdAsync(request.IntakeRecordId, cancellationToken);
        if (intakeRecord is null)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.IntakeRecordNotFound);
        }

        if (intakeRecord.LinkedTicketId is not null)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.IntakeRecordAlreadyLinked);
        }

        if (!intakeRecord.IsUnitRelated)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.IntakeRecordNotUnitRelated);
        }

        var category = await categoryRepository.GetByIdAsync(request.CategoryId, cancellationToken);
        if (category is null || !category.IsActive)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.CategoryNotFound);
        }

        var priority = await priorityRepository.GetByIdAsync(request.PriorityId, cancellationToken);
        if (priority is null)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.PriorityNotFound);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // ISSUE-006 (approved as recommended, Management-Decisions.md):
        // Medium/Low remains queued for CRM verification rather than
        // proceeding as a ticket — this is not an error, it is the correct,
        // approved outcome for those two priority tiers during an outage.
        if (!Priority.IsCriticalOrHigh(priority.PriorityId))
        {
            intakeRecord.MarkPendingCrmVerification();
            await unitOfWork.SaveChangesAsync(cancellationToken);

            await auditWriter.WriteAsync(
                callerEmployeeId, "MarkPendingCrmVerification", "IntakeRecord", intakeRecord.IntakeRecordId.ToString(),
                beforeValue: "CrmVerificationStatus=Unverified", afterValue: "CrmVerificationStatus=PendingCrmVerification",
                Guid.NewGuid(), cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return TicketCreationResult.QueuedPendingVerification(IntakeRecordAppService.ToDto(intakeRecord));
        }

        var ticketNumber = await GenerateTicketNumberAsync(category.DepartmentId, now, cancellationToken);
        var ticket = Ticket.CreateProvisional(
            ticketNumber, category.DepartmentId, category.CategoryId, priority.PriorityId, request.RequestSummary, now);

        await ticketRepository.AddAsync(ticket, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        intakeRecord.LinkToTicket(ticket.TicketId, CrmVerificationStatus.PendingCrmVerification);

        await SeedStatusHistoryAsync(ticket, callerEmployeeId, now, cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Create", "Ticket", ticket.TicketId.ToString(),
            beforeValue: null,
            afterValue: $"TicketNumber={ticket.TicketNumber};VerificationStatus=PendingCrmVerification;Provisional=true",
            Guid.NewGuid(), cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return TicketCreationResult.Success(ToDto(ticket));
    }

    /// <summary>
    /// Seeds one TicketStatusHistory row per dimension that actually has an
    /// initial value at creation (MVP-API-Contracts.md §3.1's audit note).
    /// ResolutionOutcome is deliberately excluded — it is null until the
    /// ticket is later resolved (out of scope this increment), and
    /// TicketStatusHistory.NewValue is NOT NULL (MVP-Data-Dictionary.md
    /// §2.13), so there is no value to seed a row with yet.
    /// </summary>
    private async Task SeedStatusHistoryAsync(
        Ticket ticket, Guid actorEmployeeId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        (TicketStatusDimension Dimension, byte NewValue)[] seeds =
        [
            (TicketStatusDimension.TicketStatus, (byte)ticket.TicketStatus),
            (TicketStatusDimension.VerificationStatus, (byte)ticket.VerificationStatus),
            (TicketStatusDimension.EscalationLevel, (byte)ticket.EscalationLevel),
            (TicketStatusDimension.SlaState, (byte)ticket.SlaState)
        ];

        foreach (var (dimension, newValue) in seeds)
        {
            await statusHistoryRepository.AddAsync(
                new TicketStatusHistory(
                    ticket.TicketId, dimension, oldValue: null, newValue,
                    actorEmployeeId, actorIsSystem: false, note: null, correlationId, nowUtc),
                cancellationToken);
        }
    }

    private async Task<string> GenerateTicketNumberAsync(int departmentId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var department = await departmentRepository.GetByIdAsync(departmentId, cancellationToken)
            ?? throw new InvalidOperationException($"Department {departmentId} referenced by an active Category was not found.");

        var prefix = $"TG-{department.Code}-{nowUtc:yyyyMMdd}-";
        var existingCount = await ticketRepository.CountByTicketNumberPrefixAsync(prefix, cancellationToken);
        return $"{prefix}{existingCount + 1:D4}";
    }

    private static TicketResponseDto ToDto(Ticket ticket) => new(
        ticket.TicketId,
        ticket.TicketNumber,
        ticket.OriginatingDepartmentId,
        ticket.CurrentDepartmentId,
        ticket.UnitReferenceId,
        ticket.ContactReferenceId,
        ticket.CategoryId,
        ticket.PriorityId,
        ticket.TicketStatus.ToString(),
        ticket.VerificationStatus.ToString(),
        ticket.EscalationLevel.ToString(),
        ticket.SlaState.ToString(),
        ticket.RequestSummary,
        ticket.CreatedAtUtc);
}
