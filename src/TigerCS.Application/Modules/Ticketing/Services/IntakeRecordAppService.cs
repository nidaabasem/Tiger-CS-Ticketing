using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Item 1 of this increment's scope: capture every customer interaction as
/// an IntakeRecord before any verification is attempted, so no request is
/// ever silently lost (MVP-ERD.md §2.9) — regardless of whether it turns
/// out to be unit-related (item 2) or ever becomes a ticket at all.
/// </summary>
public sealed class IntakeRecordAppService(
    IIntakeRecordRepository intakeRecordRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<IntakeRecordResult> CreateAsync(
        Guid createdByEmployeeId, CreateIntakeRecordRequestDto request, CancellationToken cancellationToken = default)
    {
        var channel = Enum.Parse<Channel>(request.ChannelId);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var intakeRecord = new IntakeRecord(
            channel, request.IsUnitRelated, request.RawUnitNumberEntered, request.PriorityHint, createdByEmployeeId, now);

        // Both SaveChanges calls below share one real transaction (senior
        // review item 11 — audit entries must be atomic with the business
        // change they describe) — see TicketCreationAppService's remarks for
        // why two calls are unavoidable (IntakeRecordId is a database-
        // generated identity column, not known until the insert commits).
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        await intakeRecordRepository.AddAsync(intakeRecord, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            createdByEmployeeId,
            "CreateIntakeRecord",
            "IntakeRecord",
            intakeRecord.IntakeRecordId.ToString(),
            beforeValue: null,
            afterValue: $"ChannelId={channel};IsUnitRelated={intakeRecord.IsUnitRelated}",
            correlationId: Guid.NewGuid(),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return IntakeRecordResult.Success(ToDto(intakeRecord));
    }

    internal static IntakeRecordResponseDto ToDto(IntakeRecord intakeRecord) => new(
        intakeRecord.IntakeRecordId,
        intakeRecord.ChannelId.ToString(),
        intakeRecord.ReceivedAtUtc,
        intakeRecord.IsUnitRelated,
        intakeRecord.RawUnitNumberEntered,
        intakeRecord.PriorityHint,
        intakeRecord.CrmVerificationStatus.ToString(),
        intakeRecord.LinkedTicketId);
}
