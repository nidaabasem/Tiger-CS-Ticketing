namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>The unconditional first step of intake (MVP-ERD.md §2.9) — created before verification, for every interaction, unit-related or not, so none is ever silently lost.</summary>
public sealed record CreateIntakeRecordRequestDto(
    string ChannelId,
    bool IsUnitRelated,
    string? RawUnitNumberEntered,
    byte? PriorityHint);

public sealed record IntakeRecordResponseDto(
    long IntakeRecordId,
    string ChannelId,
    DateTime ReceivedAtUtc,
    bool IsUnitRelated,
    string? RawUnitNumberEntered,
    byte? PriorityHint,
    string CrmVerificationStatus,
    long? LinkedTicketId);

public enum IntakeRecordOutcome
{
    Success
}

public sealed record IntakeRecordResult(IntakeRecordOutcome Outcome, IntakeRecordResponseDto? Response = null)
{
    public static IntakeRecordResult Success(IntakeRecordResponseDto response) => new(IntakeRecordOutcome.Success, response);
}
