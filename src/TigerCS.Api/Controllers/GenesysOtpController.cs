using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.CustomerVerification.Otp;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// One-time-code verification by SMS for the Genesys flow — the routes TigerGroupWeb forwards
/// (<c>api/genesys/verification/otp/{send,resend,verify}</c>). A correct code yields an ordinary confirmed
/// <c>VerificationSession</c> (method Otp, owned by the calling service account, bound to the unit), the same
/// proof <c>customers/unit-details</c> and <c>documents/send-copy</c> accept. There is no second verification system.
/// </summary>
[ApiController]
[Route("api/genesys/verification/otp")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
[Tags(OpenApiTags.Genesys)]
public class GenesysOtpController(OtpAppService otpAppService) : ControllerBase
{
    /// <summary>Send a one-time code by SMS to the mobile CRM holds for the verified customer.</summary>
    /// <remarks>
    /// The customer/number/unit are checked against CRM exactly as for <c>customers/unit-details</c>; the SMS goes to CRM's
    /// mobile for that customer, never to a caller-supplied number. The answer never contains the code. <c>status: Sent</c>
    /// means the SMS provider's response was verified as acceptance. A timeout or unreadable provider response is
    /// <c>504 OTP_DELIVERY_UNCONFIRMED</c>: the SMS may have been sent, the code stays valid, and nothing is resent
    /// automatically.
    /// </remarks>
    /// <response code="200">Sent: the provider accepted the SMS. Carries <c>otpChallengeId</c>, <c>maskedDestination</c>, <c>expiresAtUtc</c>, <c>resendAvailableAtUtc</c>.</response>
    /// <response code="400">OTP_INVALID_REQUEST.</response>
    /// <response code="403">CUSTOMER_NOT_VERIFIED or UNIT_NOT_ELIGIBLE.</response>
    /// <response code="409">CUSTOMER_AMBIGUOUS.</response>
    /// <response code="422">OTP_DESTINATION_UNAVAILABLE: CRM has no usable mobile.</response>
    /// <response code="429">OTP_RATE_LIMITED.</response>
    /// <response code="501">OTP_CHANNEL_NOT_INTEGRATED: only Sms is integrated.</response>
    /// <response code="502">OTP_DELIVERY_REJECTED, OTP_DELIVERY_FAILED or CRM_UNAVAILABLE.</response>
    /// <response code="503">OTP_DISABLED or OTP_SMS_NOT_CONFIGURED.</response>
    /// <response code="504">OTP_DELIVERY_UNCONFIRMED.</response>
    [HttpPost("send")]
    [ProducesResponseType<OtpResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status501NotImplemented)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Send([FromBody] OtpSendRequestDto request, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is not { } caller)
        {
            return Unauthorized();
        }

        return Map(await otpAppService.SendAsync(caller, request, cancellationToken));
    }

    /// <summary>Issue a replacement code for an existing request (the previous code stops working).</summary>
    /// <remarks>Only the account that sent the first code can resend. Limited by the configured cooldown and send count. Never called automatically after an unconfirmed send.</remarks>
    /// <response code="200">Sent.</response>
    /// <response code="404">OTP_CHALLENGE_NOT_FOUND (unknown or someone else's).</response>
    /// <response code="409">OTP_CHALLENGE_NOT_ACTIVE or OTP_CONFLICT.</response>
    /// <response code="429">OTP_RESEND_TOO_SOON (see Retry-After) or OTP_RESEND_LIMIT.</response>
    [HttpPost("resend")]
    [ProducesResponseType<OtpResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Resend([FromBody] OtpResendRequestDto request, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is not { } caller)
        {
            return Unauthorized();
        }

        return Map(await otpAppService.ResendAsync(caller, request, cancellationToken));
    }

    /// <summary>Check the code. On success, returns the confirmed <c>verificationSessionId</c> to send with <c>customers/unit-details</c>.</summary>
    /// <response code="200">Verified: <c>verificationSessionId</c>, <c>verificationSessionExpiresAtUtc</c>, <c>unitId</c>.</response>
    /// <response code="400">OTP_INVALID_CODE (with <c>attemptsRemaining</c>).</response>
    /// <response code="404">OTP_CHALLENGE_NOT_FOUND.</response>
    /// <response code="409">OTP_CHALLENGE_NOT_ACTIVE or OTP_CONFLICT (already used).</response>
    /// <response code="410">OTP_EXPIRED.</response>
    /// <response code="429">OTP_LOCKED: too many wrong codes.</response>
    [HttpPost("verify")]
    [ProducesResponseType<OtpResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Verify([FromBody] OtpVerifyRequestDto request, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is not { } caller)
        {
            return Unauthorized();
        }

        return Map(await otpAppService.VerifyAsync(caller, request, cancellationToken));
    }

    private IActionResult Map(OtpResult result)
    {
        Response.Headers.CacheControl = "no-store";

        return result.Status switch
        {
            OtpStatus.Sent or OtpStatus.Verified => Ok(result),

            OtpStatus.Disabled => Coded(result, 503, "OTP_DISABLED", "One-time codes are switched off"),
            OtpStatus.SmsNotConfigured => Coded(result, 503, "OTP_SMS_NOT_CONFIGURED", "SMS delivery is not configured"),
            OtpStatus.ChannelNotIntegrated => Coded(result, 501, "OTP_CHANNEL_NOT_INTEGRATED", "Delivery channel not integrated"),
            OtpStatus.InvalidRequest => Coded(result, 400, "OTP_INVALID_REQUEST", "Invalid one-time-code request"),
            OtpStatus.CustomerNotVerified => Coded(result, 403, "CUSTOMER_NOT_VERIFIED", "The customer could not be verified"),
            OtpStatus.UnitNotEligible => Coded(result, 403, "UNIT_NOT_ELIGIBLE", "The unit is not available to this customer"),
            OtpStatus.CustomerAmbiguous => Coded(result, 409, "CUSTOMER_AMBIGUOUS", "More than one customer matches the number"),
            OtpStatus.CrmUnavailable => Coded(result, 502, "CRM_UNAVAILABLE", "CRM is currently unavailable"),
            OtpStatus.DestinationUnavailable => Coded(result, 422, "OTP_DESTINATION_UNAVAILABLE", "No usable mobile number on record"),
            OtpStatus.RateLimited => Coded(result, 429, "OTP_RATE_LIMITED", "Too many code requests"),
            OtpStatus.ResendTooSoon => WithRetryAfter(Coded(result, 429, "OTP_RESEND_TOO_SOON", "Resend not yet available"), result),
            OtpStatus.ResendLimitReached => Coded(result, 429, "OTP_RESEND_LIMIT", "Resend limit reached"),
            OtpStatus.DeliveryRejected => Coded(result, 502, "OTP_DELIVERY_REJECTED", "The SMS provider refused the message"),
            OtpStatus.DeliveryFailed => Coded(result, 502, "OTP_DELIVERY_FAILED", "The SMS could not be sent"),
            OtpStatus.DeliveryUnconfirmed => Coded(result, 504, "OTP_DELIVERY_UNCONFIRMED", "SMS delivery could not be confirmed"),
            OtpStatus.ChallengeNotFound => Coded(result, 404, "OTP_CHALLENGE_NOT_FOUND", "No such code request"),
            OtpStatus.ChallengeNotActive => Coded(result, 409, "OTP_CHALLENGE_NOT_ACTIVE", "The code request is no longer active"),
            OtpStatus.Expired => Coded(result, 410, "OTP_EXPIRED", "The code has expired"),
            OtpStatus.InvalidCode => Coded(result, 400, "OTP_INVALID_CODE", "The code is not correct"),
            OtpStatus.Locked => Coded(result, 429, "OTP_LOCKED", "Too many wrong codes"),
            OtpStatus.Conflict => Coded(result, 409, "OTP_CONFLICT", "The request conflicted with another"),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private ObjectResult Coded(OtpResult result, int statusCode, string code, string title)
    {
        var problem = Problem(
            type: $"https://tigercs.internal/problems/{code.ToLowerInvariant().Replace('_', '-')}",
            title: title, detail: result.Message, statusCode: statusCode);

        if (problem.Value is ProblemDetails details)
        {
            details.Extensions["code"] = code;
            details.Extensions["outcome"] = result.Status.ToString();
            if (result.OtpChallengeId is not null) details.Extensions["otpChallengeId"] = result.OtpChallengeId;
            if (result.Channel is not null) details.Extensions["channel"] = result.Channel;
            if (result.MaskedDestination is not null) details.Extensions["maskedDestination"] = result.MaskedDestination;
            if (result.ExpiresAtUtc is not null) details.Extensions["expiresAtUtc"] = result.ExpiresAtUtc;
            if (result.ResendAvailableAtUtc is not null) details.Extensions["resendAvailableAtUtc"] = result.ResendAvailableAtUtc;
            if (result.AttemptsRemaining is not null) details.Extensions["attemptsRemaining"] = result.AttemptsRemaining;
            if (result.SendsRemaining is not null) details.Extensions["sendsRemaining"] = result.SendsRemaining;
            if (result.Retryable is not null) details.Extensions["retryable"] = result.Retryable;
        }

        return problem;
    }

    private ObjectResult WithRetryAfter(ObjectResult problem, OtpResult result)
    {
        if (result.ResendAvailableAtUtc is { } at)
        {
            var seconds = (int)Math.Ceiling(Math.Max(1, (at - DateTime.UtcNow).TotalSeconds));
            Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return problem;
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}
