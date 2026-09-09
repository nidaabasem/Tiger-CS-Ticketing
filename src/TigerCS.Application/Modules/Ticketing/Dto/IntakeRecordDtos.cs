namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>The unconditional first step of intake (MVP-ERD.md §2.9) — created before customer lookup, for every interaction, unit-related or not, so none is ever silently lost.</summary>
/// <param name="ChannelId">
/// Required. The selected channel from <c>GET /api/channels</c> — its
/// numeric <c>channelId</c> (canonical), or its stable <c>code</c> (the
/// seeded codes are Phone, AppOrWebsite, WhatsAppOrLiveChat,
/// SocialMediaDirectMessage, FaceToFaceKiosk; codes match
/// case-insensitively). The channel must exist and be active for a new
/// ticket; it is configured under Admin → Configuration → Channels.
/// </param>
/// <param name="PhoneNumber">
/// The identifier customer lookup searches CRM/PACT/Tasleeh with. Required
/// when the selected channel's <c>requiresPhone</c> configuration is true
/// (e.g. Phone), optional otherwise (e.g. Face to Face / Kiosk). Preserved
/// exactly as entered, regardless of what the lookup finds.
/// </param>
/// <param name="DepartmentId">
/// Optional. When given, customer lookup searches only the source(s)
/// configured for this Department instead of CRM+PACT+Tasleeh. Never a
/// promotion gate — a Department-scoped intake still promotes to a ticket
/// exactly like any other.
/// </param>
/// <param name="IsUnitRelated">
/// Required. Whether the interaction is already known to concern a specific
/// unit. The current New Ticket wizard always sends false here — the
/// authoritative classification instead comes from a Unit selected via
/// customer lookup after this call, which upgrades it automatically; this
/// flag exists for a caller that already knows before lookup runs.
/// </param>
/// <param name="RawUnitNumberEntered">
/// Optional, and independent of <paramref name="IsUnitRelated"/> — never
/// required for it, never required to be absent without it. The unit number
/// exactly as the caller said it, before any lookup, kept only as an
/// audit/intake note when it happens to be captured. Never the source of
/// unit-related classification or ticket routing — the authoritative Unit is
/// always the one resolved through customer lookup.
/// </param>
/// <param name="PriorityHint">Optional. 1=Critical, 2=High, 3=Medium, 4=Low.</param>
public sealed record CreateIntakeRecordRequestDto(
    string ChannelId,
    string PhoneNumber,
    int? DepartmentId,
    bool IsUnitRelated,
    string? RawUnitNumberEntered,
    byte? PriorityHint);

/// <summary>A recorded intake (MVP-ERD.md §2.9).</summary>
/// <param name="IntakeRecordId">The intake record. Pass this to customer lookup and ticket creation.</param>
/// <param name="ChannelId">The stable code of the channel the interaction arrived on (e.g. Phone). See <paramref name="ChannelName"/> for its display name.</param>
/// <param name="ChannelName">The display name of the channel the interaction arrived on.</param>
/// <param name="ReceivedAtUtc">When the interaction was recorded, in UTC.</param>
/// <param name="PhoneNumber">The identifier customer lookup searches CRM/PACT/Tasleeh with.</param>
/// <param name="DepartmentId">When set, narrows customer lookup to this Department's configured source(s).</param>
/// <param name="IsUnitRelated">
/// Whether the interaction is associated with a selected Unit. False at
/// creation for the current wizard; upgraded automatically once a Unit is
/// selected via customer lookup and linked to a ticket — never inferred from
/// <paramref name="RawUnitNumberEntered"/> alone.
/// </param>
/// <param name="RawUnitNumberEntered">The unit number as the caller gave it, before any lookup — an optional historical note only, independent of <paramref name="IsUnitRelated"/>, never the authoritative Unit.</param>
/// <param name="PriorityHint">The agent's priority hint, if given. 1=Critical, 2=High, 3=Medium, 4=Low.</param>
/// <param name="CrmVerificationStatus">One of Unverified, PendingCrmVerification, Verified.</param>
/// <param name="LinkedTicketId">The ticket this intake was promoted to, or null while it has not been.</param>
public sealed record IntakeRecordResponseDto(
    long IntakeRecordId,
    string ChannelId,
    DateTime ReceivedAtUtc,
    string PhoneNumber,
    int? DepartmentId,
    bool IsUnitRelated,
    string? RawUnitNumberEntered,
    byte? PriorityHint,
    string CrmVerificationStatus,
    long? LinkedTicketId,
    string? ChannelName = null);

public enum IntakeRecordOutcome
{
    Success,

    /// <summary>DepartmentId was supplied but does not reference a real Department.</summary>
    DepartmentNotFound,

    /// <summary>ChannelId does not reference a configured channel (neither by id nor by code).</summary>
    ChannelNotFound,

    /// <summary>The channel exists but is deactivated — it may not be selected for a new ticket.</summary>
    ChannelInactive,

    /// <summary>The selected channel's configuration requires a phone number and none was supplied.</summary>
    PhoneNumberRequired
}

public sealed record IntakeRecordResult(IntakeRecordOutcome Outcome, IntakeRecordResponseDto? Response = null)
{
    public static IntakeRecordResult Success(IntakeRecordResponseDto response) => new(IntakeRecordOutcome.Success, response);
    public static IntakeRecordResult Failure(IntakeRecordOutcome outcome) => new(outcome);
}
