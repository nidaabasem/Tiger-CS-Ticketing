namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>The unconditional first step of intake (MVP-ERD.md §2.9) — created before verification, for every interaction, unit-related or not, so none is ever silently lost.</summary>
/// <param name="ChannelId">Required. One of Phone, AppOrWebsite, WhatsAppOrLiveChat, SocialMediaDirectMessage, FaceToFaceKiosk. Case-sensitive. MVP scope is Phone.</param>
/// <param name="IsUnitRelated">Required. Whether the interaction concerns a specific unit.</param>
/// <param name="RawUnitNumberEntered">Required when <paramref name="IsUnitRelated"/> is true, and must be omitted when it is false — exactly as the caller typed it, before any CRM lookup.</param>
/// <param name="PriorityHint">Optional. 1=Critical, 2=High, 3=Medium, 4=Low.</param>
public sealed record CreateIntakeRecordRequestDto(
    string ChannelId,
    bool IsUnitRelated,
    string? RawUnitNumberEntered,
    byte? PriorityHint);

/// <summary>A recorded intake (MVP-ERD.md §2.9).</summary>
/// <param name="IntakeRecordId">The intake record. Pass this to ticket creation.</param>
/// <param name="ChannelId">The channel the interaction arrived on.</param>
/// <param name="ReceivedAtUtc">When the interaction was recorded, in UTC.</param>
/// <param name="IsUnitRelated">Whether the interaction concerns a specific unit.</param>
/// <param name="RawUnitNumberEntered">The unit number as the caller gave it, before any CRM lookup.</param>
/// <param name="PriorityHint">The agent's priority hint, if given. 1=Critical, 2=High, 3=Medium, 4=Low.</param>
/// <param name="CrmVerificationStatus">One of Unverified, PendingCrmVerification, Verified.</param>
/// <param name="LinkedTicketId">The ticket this intake was promoted to, or null while it has not been.</param>
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
