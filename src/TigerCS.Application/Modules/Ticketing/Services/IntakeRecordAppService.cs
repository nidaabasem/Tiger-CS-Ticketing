using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Item 1 of this increment's scope: capture every customer interaction as
/// an IntakeRecord before any customer lookup is attempted, so no request is
/// ever silently lost (MVP-ERD.md §2.9) — regardless of whether it turns
/// out to be unit-related or ever becomes a ticket at all.
/// </summary>
public sealed class IntakeRecordAppService(
    IIntakeRecordRepository intakeRecordRepository,
    IDepartmentRepository departmentRepository,
    IChannelRepository channelRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    /// <param name="createdByEmployeeId">The acting employee — the intake's recorded author.</param>
    /// <param name="request">The channel, phone number, optional department, and the raw intake notes.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <param name="enforceChannelPhoneRequirement">
    /// Whether the selected channel's <c>RequiresPhone</c> setting is
    /// enforced. True — the default, and what every agent-driven caller uses
    /// — is the Create Ticket Step 1 rule: the wizard must not let an agent
    /// proceed without a number the channel needs.
    ///
    /// <para>
    /// <b>False is for an automated intake, and exists so an inquiry is never
    /// discarded.</b> A Genesys inquiry records whatever the channel actually
    /// supplied: a caller who withheld their number, or a social-media direct
    /// message that carries none, has no phone to capture, and refusing the
    /// intake would throw away a real customer interaction — the exact
    /// outcome <see cref="IntakeRecord"/> exists to prevent ("no interaction
    /// is ever silently lost"). The setting still governs every agent-facing
    /// path unchanged; nothing about the stored record differs, and a phone
    /// that IS supplied is stored and searched exactly as always.
    /// </para>
    /// </param>
    public async Task<IntakeRecordResult> CreateAsync(
        Guid createdByEmployeeId,
        CreateIntakeRecordRequestDto request,
        CancellationToken cancellationToken = default,
        bool enforceChannelPhoneRequirement = true)
    {
        if (request.DepartmentId is { } departmentId
            && await departmentRepository.GetByIdAsync(departmentId, cancellationToken) is null)
        {
            return IntakeRecordResult.Failure(IntakeRecordOutcome.DepartmentNotFound);
        }

        // The channel is CONFIGURATION (Admin → Configuration → Channels),
        // resolved here — never a hard-coded list or a name comparison. It
        // must exist and be active for a new ticket, and its own
        // RequiresPhone setting decides whether the phone number is
        // mandatory (a Phone call has one; a kiosk walk-in may not).
        var channel = await ResolveChannelAsync(request.ChannelId, cancellationToken);
        if (channel is null)
        {
            return IntakeRecordResult.Failure(IntakeRecordOutcome.ChannelNotFound);
        }

        if (!channel.IsActive)
        {
            return IntakeRecordResult.Failure(IntakeRecordOutcome.ChannelInactive);
        }

        if (enforceChannelPhoneRequirement && channel.RequiresPhone && string.IsNullOrWhiteSpace(request.PhoneNumber))
        {
            return IntakeRecordResult.Failure(IntakeRecordOutcome.PhoneNumberRequired);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // The phone travels verbatim (never reformatted); a blank one on a
        // channel that does not require it is stored as empty.
        var phoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? string.Empty : request.PhoneNumber;

        var intakeRecord = new IntakeRecord(
            channel.ChannelId, phoneNumber, request.DepartmentId, request.IsUnitRelated,
            request.RawUnitNumberEntered, request.PriorityHint, createdByEmployeeId, now);

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
            afterValue: $"ChannelId={channel.ChannelId};ChannelCode={channel.Code};IsUnitRelated={intakeRecord.IsUnitRelated}",
            correlationId: Guid.NewGuid(),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return IntakeRecordResult.Success(ToDto(intakeRecord, channel));
    }

    /// <summary>
    /// Accepts the canonical numeric ChannelId, or — for API backward
    /// compatibility with the former enum contract — the channel's stable
    /// Code (case-insensitive). Returns null when neither resolves.
    /// </summary>
    private async Task<Channel?> ResolveChannelAsync(string? channelId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return null;
        }

        var value = channelId.Trim();
        if (byte.TryParse(value, out var id))
        {
            return await channelRepository.GetByIdAsync(id, cancellationToken);
        }

        return await channelRepository.GetByCodeAsync(value, cancellationToken);
    }

    internal static IntakeRecordResponseDto ToDto(IntakeRecord intakeRecord, Channel channel) => new(
        intakeRecord.IntakeRecordId,
        channel.Code,
        intakeRecord.ReceivedAtUtc,
        intakeRecord.PhoneNumber,
        intakeRecord.DepartmentId,
        intakeRecord.IsUnitRelated,
        intakeRecord.RawUnitNumberEntered,
        intakeRecord.PriorityHint,
        intakeRecord.CrmVerificationStatus.ToString(),
        intakeRecord.LinkedTicketId,
        channel.Name);
}
