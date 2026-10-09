using System.Text.Json.Serialization;
using TigerCS.Application.Modules.CustomerVerification.Dto;

namespace TigerCS.Application.Modules.CrmDocuments.Dto;

/// <param name="PhoneNumber">The number the customer is calling/chatting from — only the key CRM is searched by. It proves nothing; the code emailed to CRM's address does.</param>
public sealed record BuyerLookupRequestDto(string? PhoneNumber);

/// <param name="PhoneNumber">As for the lookup.</param>
/// <param name="CrmUnitId">The unit the customer chose (a <c>crmUnitId</c> from the lookup). Omitted only when the customer has exactly one eligible unit.</param>
public sealed record OtpSendRequestDto(string? PhoneNumber, string? CrmUnitId = null);

public sealed record OtpResendRequestDto(Guid? ChallengeId);

/// <param name="ChallengeId">From the send/resend answer.</param>
/// <param name="Code">The 6-digit code the customer received by email.</param>
public sealed record OtpVerifyRequestDto(Guid? ChallengeId, string? Code);

/// <summary>A unit the customer may choose, from CRM's buyer lookup. <c>CrmUnitId</c> is CRM's <c>unitId</c>.</summary>
public sealed record BuyerUnitChoice(string CrmUnitId, int LeadId, string UnitNumber, string? ProjectName);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerOtpStatus
{
    /// <summary>Lookup: a single CRM buyer was found (units listed).</summary>
    Found,

    /// <summary>A code was emailed to the address CRM holds.</summary>
    CodeSent,

    /// <summary>An unexpired challenge for this customer and unit already exists; nothing new was sent.</summary>
    AlreadySent,

    /// <summary>The code was right: <c>session</c> is the OTP-verified verification session.</summary>
    Verified,

    /// <summary>The customer has several eligible units; <c>units</c> lists them. Nothing was sent.</summary>
    UnitSelectionRequired,

    CustomerNotFound,
    CustomerAmbiguous,
    NoEmailOnRecord,
    UnitNotOwned,
    InvalidCode,
    Expired,
    Locked,
    AlreadyUsed,
    RateLimited,
    ResendTooSoon,
    ResendLimitReached,
    ChallengeNotFound,
    DeliveryFailed,
    CrmUnavailable,
    CrmAuthenticationFailed,
    CrmInvalidResponse,
    InvalidRequest,
    Disabled
}

/// <summary>
/// The outcome of a buyer-verification step. Identifiers and status only —
/// never the code, never the full email address, never CRM's customer id.
/// </summary>
public sealed record CustomerOtpResult(
    CustomerOtpStatus Status,
    string? Code = null,
    string? Message = null,
    Guid? ChallengeId = null,
    string? MaskedDestination = null,
    DateTime? ExpiresAtUtc = null,
    DateTime? ResendAvailableAtUtc = null,
    int? AttemptsRemaining = null,
    int? RetryAfterSeconds = null,
    IReadOnlyList<BuyerUnitChoice>? Units = null,
    BuyerUnitChoice? Unit = null,
    VerificationSessionResponseDto? Session = null);

public static class CustomerOtpCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string Disabled = "DOCUMENT_COPY_DISABLED";
    public const string CustomerNotFound = "CUSTOMER_NOT_FOUND";
    public const string CustomerAmbiguous = "CUSTOMER_AMBIGUOUS";
    public const string NoEmailOnRecord = "NO_EMAIL_ON_RECORD";
    public const string UnitNotOwned = "UNIT_NOT_OWNED";
    public const string UnitSelectionRequired = "UNIT_SELECTION_REQUIRED";
    public const string OtpInvalid = "OTP_INVALID";
    public const string OtpExpired = "OTP_EXPIRED";
    public const string OtpLocked = "OTP_LOCKED";
    public const string OtpAlreadyUsed = "OTP_ALREADY_USED";
    public const string OtpRateLimited = "OTP_RATE_LIMITED";
    public const string OtpResendTooSoon = "OTP_RESEND_TOO_SOON";
    public const string OtpResendLimit = "OTP_RESEND_LIMIT_REACHED";
    public const string ChallengeNotFound = "OTP_CHALLENGE_NOT_FOUND";
    public const string DeliveryFailed = "OTP_DELIVERY_FAILED";
    public const string CrmUnavailable = "CRM_UNAVAILABLE";
    public const string CrmAuthenticationFailed = "CRM_AUTHENTICATION_FAILED";
    public const string CrmInvalidResponse = "CRM_INVALID_RESPONSE";
}
