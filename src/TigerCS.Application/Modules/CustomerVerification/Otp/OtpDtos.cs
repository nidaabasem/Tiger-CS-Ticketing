using System.Text.Json.Serialization;

namespace TigerCS.Application.Modules.CustomerVerification.Otp;

/// <param name="CustomerReference">The CRM customer (<c>crm:{id}</c> or the plain id) — checked against CRM, never trusted.</param>
/// <param name="PhoneNumber">The number the customer was identified by. It is how CRM is asked who the customer is; the code is NOT sent to it unless it is the mobile CRM holds.</param>
/// <param name="UnitId">The CRM unit the customer wants to talk about; must be one of their own units.</param>
/// <param name="Channel">Only <c>Sms</c> is integrated (default).</param>
/// <param name="Language"><c>en</c> or <c>ar</c>; the SMS text language.</param>
public sealed record OtpSendRequestDto(string? CustomerReference, string? PhoneNumber, int? UnitId, string? Channel = null, string? Language = null);

/// <param name="OtpChallengeId">From the send answer.</param>
/// <param name="Language">Optional: change the language of the new SMS.</param>
public sealed record OtpResendRequestDto(Guid? OtpChallengeId, string? Language = null);

public sealed record OtpVerifyRequestDto(Guid? OtpChallengeId, string? Code);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OtpStatus
{
    /// <summary>Send/resend: the provider accepted the SMS (verified response).</summary>
    Sent,

    /// <summary>Verify: correct code; a confirmed verification session was recorded.</summary>
    Verified,

    Disabled,
    SmsNotConfigured,
    ChannelNotIntegrated,
    InvalidRequest,
    CustomerNotVerified,
    UnitNotEligible,
    CustomerAmbiguous,
    CrmUnavailable,
    DestinationUnavailable,
    RateLimited,
    ResendTooSoon,
    ResendLimitReached,
    DeliveryRejected,
    DeliveryFailed,
    DeliveryUnconfirmed,
    ChallengeNotFound,
    ChallengeNotActive,
    Expired,
    InvalidCode,
    Locked,
    Conflict
}

/// <summary>
/// The answer to send / resend / verify. <b>The code is never in it.</b>
/// <see cref="Status"/> <c>Sent</c> means the provider's response was verified as acceptance;
/// <c>DeliveryUnconfirmed</c> means the SMS may or may not have gone out (the challenge stays
/// valid, nothing is resent automatically).
/// </summary>
public sealed record OtpResult(
    OtpStatus Status,
    string? Message = null,
    Guid? OtpChallengeId = null,
    string? Channel = null,
    string? MaskedDestination = null,
    DateTime? ExpiresAtUtc = null,
    DateTime? ResendAvailableAtUtc = null,
    int? AttemptsRemaining = null,
    int? SendsRemaining = null,
    Guid? VerificationSessionId = null,
    DateTime? VerificationSessionExpiresAtUtc = null,
    int? UnitId = null,
    string? CustomerReference = null,
    bool? Retryable = null);
