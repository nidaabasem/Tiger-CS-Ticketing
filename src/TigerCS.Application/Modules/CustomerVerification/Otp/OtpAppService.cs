using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.GenesysIntegration;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.CustomerVerification.Otp;

/// <summary>
/// Issues, resends and checks one-time codes, and turns a correct code into the
/// <b>existing</b> proof: a confirmed <see cref="VerificationSession"/> (method Otp, owned by
/// the calling service account, bound to the customer's unit, 30-minute lifetime) that the
/// unit-details and document-copy APIs already accept. This is not a second verification system.
///
/// <list type="bullet">
/// <item><b>Who/what:</b> ownership uses <see cref="GenesysVerifiedBuyerResolver"/> — the same rule as unit-details. The code goes to the mobile <i>CRM</i> holds for the verified customer, never to a number the caller supplies.</item>
/// <item><b>The code:</b> random, never stored (HMAC-SHA-256 with a server pepper), never returned, logged or audited.</item>
/// <item><b>Delivery honesty:</b> <c>Sent</c> only for a verified provider acceptance. A timeout or unreadable response is <c>DeliveryUnconfirmed</c>: the challenge stays valid (the SMS may have arrived), nothing is resent automatically, and only an explicit resend — after the cooldown — issues a new code.</item>
/// <item><b>Limits:</b> expiry, wrong-code attempts, sends per challenge, resend cooldown, challenges per customer per hour — all <see cref="OtpOptions"/>.</item>
/// </list>
/// </summary>
public sealed class OtpAppService(
    OtpOptions options,
    GenesysOptions genesysOptions,
    GenesysVerifiedBuyerResolver buyerResolver,
    ISmsSender smsSender,
    IOtpChallengeRepository challenges,
    IVerificationSessionRepository sessions,
    IUnitReferenceRepository units,
    IContactReferenceRepository contacts,
    ICustomerVerificationUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<OtpAppService> logger)
{
    public const string SmsChannel = "Sms";

    public async Task<OtpResult> SendAsync(Guid callerEmployeeId, OtpSendRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!genesysOptions.Enabled || !options.Enabled)
        {
            return new(OtpStatus.Disabled, "One-time codes are switched off.");
        }

        if (string.IsNullOrWhiteSpace(options.Pepper) || !smsSender.IsConfigured)
        {
            return new(OtpStatus.SmsNotConfigured, "The SMS provider or the code secret is not configured. No code was created.");
        }

        if (!string.IsNullOrWhiteSpace(request.Channel) && !string.Equals(request.Channel.Trim(), SmsChannel, StringComparison.OrdinalIgnoreCase))
        {
            return new(OtpStatus.ChannelNotIntegrated, "Only the Sms channel is integrated.");
        }

        if (request.UnitId is not > 0)
        {
            return new(OtpStatus.InvalidRequest, "unitId is required and must be a positive CRM unit id.");
        }

        if (!TryLanguage(request.Language, out var language))
        {
            return new(OtpStatus.InvalidRequest, "language must be en or ar.");
        }

        var resolved = await buyerResolver.ResolveAsync(request.CustomerReference, request.PhoneNumber, request.UnitId, cancellationToken);
        switch (resolved.Outcome)
        {
            case VerifiedBuyerOutcome.Resolved:
                break;
            case VerifiedBuyerOutcome.CustomerReferenceInvalid:
            case VerifiedBuyerOutcome.PhoneNumberInvalid:
            case VerifiedBuyerOutcome.UnitIdInvalid:
                return new(OtpStatus.InvalidRequest, "customerReference, phoneNumber and unitId are required and must be well-formed.");
            case VerifiedBuyerOutcome.CustomerNotVerified:
                return new(OtpStatus.CustomerNotVerified, "The customer reference does not match a customer CRM holds for this number.");
            case VerifiedBuyerOutcome.UnitNotEligible:
                return new(OtpStatus.UnitNotEligible, "The requested unit is not one of this customer's units.");
            case VerifiedBuyerOutcome.AmbiguousCustomer:
                return new(OtpStatus.CustomerAmbiguous, "More than one customer matches the number. Nothing was sent; hand over to an agent.");
            default:
                return new(OtpStatus.CrmUnavailable, "Tiger CRM could not be reached.");
        }

        var destination = NormalizeDestination(resolved.Buyer!.Customer.MobileNumber);
        if (destination is null)
        {
            return new(OtpStatus.DestinationUnavailable, "CRM holds no usable mobile number for this customer, so no code can be sent.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var recent = await challenges.CountForCustomerSinceAsync(resolved.CustomerId, now.AddHours(-1), cancellationToken);
        if (recent >= Math.Max(1, options.MaxChallengesPerCustomerPerHour))
        {
            return new(OtpStatus.RateLimited, "Too many codes were requested for this customer. Try again later.", Retryable: false);
        }

        var challengeId = Guid.NewGuid();
        var code = NewCode();
        var challenge = new OtpChallenge(
            challengeId, callerEmployeeId, resolved.CustomerId, resolved.Unit!.UnitId, resolved.Unit.UnitNumber, resolved.Unit.ProjectName,
            SmsChannel, destination, language, Hash(challengeId, code), now, Lifetime());

        // Recorded BEFORE the provider is called, so a timeout or crash leaves a traceable challenge.
        await challenges.AddAsync(challenge, cancellationToken);
        if (!await challenges.SaveChangesAsync(cancellationToken))
        {
            return new(OtpStatus.Conflict, "The request conflicted with another; retry.");
        }

        return await DeliverAsync(callerEmployeeId, challenge, code, "SendOtp", cancellationToken);
    }

    public async Task<OtpResult> ResendAsync(Guid callerEmployeeId, OtpResendRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!genesysOptions.Enabled || !options.Enabled)
        {
            return new(OtpStatus.Disabled, "One-time codes are switched off.");
        }

        if (string.IsNullOrWhiteSpace(options.Pepper) || !smsSender.IsConfigured)
        {
            return new(OtpStatus.SmsNotConfigured, "The SMS provider or the code secret is not configured.");
        }

        if (request.OtpChallengeId is not { } id || id == Guid.Empty)
        {
            return new(OtpStatus.InvalidRequest, "otpChallengeId is required.");
        }

        if (!TryLanguage(request.Language, out var requestedLanguage, allowBlank: true))
        {
            return new(OtpStatus.InvalidRequest, "language must be en or ar.");
        }

        var challenge = await challenges.GetAsync(id, cancellationToken);
        if (challenge is null || !challenge.IsOwnedBy(callerEmployeeId))
        {
            return new(OtpStatus.ChallengeNotFound, "No such code request.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (challenge.Status is OtpChallengeStatus.Verified or OtpChallengeStatus.Locked
            || now >= challenge.CreatedAtUtc.AddMinutes(Math.Max(1, options.MaxChallengeAgeMinutes)))
        {
            return new(OtpStatus.ChallengeNotActive, "This code request can no longer be resent. Start a new one.", id);
        }

        if (challenge.SendCount >= Math.Max(1, options.MaxSendsPerChallenge))
        {
            return new(OtpStatus.ResendLimitReached, "The resend limit was reached. Start a new code request.", id, Retryable: false);
        }

        var available = challenge.LastSentAtUtc.AddSeconds(Math.Max(0, options.ResendCooldownSeconds));
        if (now < available)
        {
            return new(OtpStatus.ResendTooSoon, "Wait before requesting another code.", id, ResendAvailableAtUtc: available, Retryable: true);
        }

        var code = NewCode();
        challenge.ReplaceCode(Hash(challenge.OtpChallengeId, code), now, Lifetime(), requestedLanguage);

        if (!await challenges.SaveChangesAsync(cancellationToken))
        {
            return new(OtpStatus.Conflict, "The request conflicted with another; retry.", id);
        }

        return await DeliverAsync(callerEmployeeId, challenge, code, "ResendOtp", cancellationToken);
    }

    public async Task<OtpResult> VerifyAsync(Guid callerEmployeeId, OtpVerifyRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!genesysOptions.Enabled || !options.Enabled)
        {
            return new(OtpStatus.Disabled, "One-time codes are switched off.");
        }

        if (string.IsNullOrWhiteSpace(options.Pepper))
        {
            return new(OtpStatus.SmsNotConfigured, "The code secret is not configured.");
        }

        if (request.OtpChallengeId is not { } id || id == Guid.Empty || string.IsNullOrWhiteSpace(request.Code))
        {
            return new(OtpStatus.InvalidRequest, "otpChallengeId and code are required.");
        }

        var challenge = await challenges.GetAsync(id, cancellationToken);
        if (challenge is null || !challenge.IsOwnedBy(callerEmployeeId))
        {
            return new(OtpStatus.ChallengeNotFound, "No such code request.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        switch (challenge.Status)
        {
            case OtpChallengeStatus.Locked:
                return new(OtpStatus.Locked, "Too many wrong codes. Start a new code request.", id, AttemptsRemaining: 0);
            case OtpChallengeStatus.Verified:
            case OtpChallengeStatus.DeliveryFailed:
                return new(OtpStatus.ChallengeNotActive, "This code request cannot be verified.", id);
        }

        if (challenge.IsExpired(now) || now >= challenge.CreatedAtUtc.AddMinutes(Math.Max(1, options.MaxChallengeAgeMinutes)))
        {
            return new(OtpStatus.Expired, "The code has expired. Request a new one.", id);
        }

        var supplied = Hash(id, request.Code.Trim());
        var matches = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(challenge.CodeHash));
        if (!matches)
        {
            var left = challenge.RegisterWrongCode(Math.Max(1, options.MaxVerifyAttempts));
            await auditWriter.WriteAsync(callerEmployeeId, "VerifyOtpFailed", nameof(OtpChallenge), id.ToString(), null,
                $"AttemptsRemaining={left}", Guid.NewGuid(), cancellationToken);
            if (!await challenges.SaveChangesAsync(cancellationToken))
            {
                return new(OtpStatus.Conflict, "The request conflicted with another; retry.", id);
            }

            return left == 0
                ? new(OtpStatus.Locked, "Too many wrong codes. Start a new code request.", id, AttemptsRemaining: 0)
                : new(OtpStatus.InvalidCode, "The code is not correct.", id, AttemptsRemaining: left);
        }

        // Correct code: record the ordinary, confirmed VerificationSession.
        var crmUnitId = challenge.CrmUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var unit = await GetOrAddUnitAsync(challenge, crmUnitId, now, cancellationToken);
        var contact = await GetOrAddContactAsync(challenge, unit.UnitReferenceId, crmUnitId, now, cancellationToken);

        var session = new VerificationSession(
            Guid.NewGuid(), challenge.OwnerEmployeeId, unit.UnitReferenceId, contact.ContactReferenceId,
            unit.UnitNumber, unit.PropertyName, null, null, null, challenge.Channel,
            now, now.Add(VerificationSessionAppService.SessionLifetime), null);
        session.Confirm(now, VerificationMethod.Otp);

        await sessions.AddAsync(session, cancellationToken);
        challenge.MarkVerified(session.VerificationSessionId);
        await auditWriter.WriteAsync(callerEmployeeId, "VerifyOtp", nameof(OtpChallenge), id.ToString(), null,
            $"CrmCustomerId={challenge.CrmCustomerId};CrmUnitId={challenge.CrmUnitId};VerificationSessionId={session.VerificationSessionId}",
            Guid.NewGuid(), cancellationToken);

        if (!await challenges.SaveChangesAsync(cancellationToken))
        {
            // Another request verified this challenge first; nothing from this attempt was persisted.
            return new(OtpStatus.Conflict, "The code was already used.", id);
        }

        return new(
            OtpStatus.Verified, null, id, challenge.Channel, VerificationSessionId: session.VerificationSessionId,
            VerificationSessionExpiresAtUtc: session.ExpiresAtUtc, UnitId: challenge.CrmUnitId,
            CustomerReference: CustomerIdentity.Crm(challenge.CrmCustomerId).Key);
    }

    // ---------------------------------------------------------------- delivery

    private async Task<OtpResult> DeliverAsync(
        Guid callerEmployeeId, OtpChallenge challenge, string code, string action, CancellationToken cancellationToken)
    {
        var text = Compose(challenge.Language, code);
        SmsSendResult result;
        try
        {
            result = await smsSender.SendAsync(new SmsMessage(challenge.Destination, text, challenge.Language), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A sender must not throw for provider answers; an unexpected fault means we do not know what happened.
            logger.LogError("SMS sender faulted for challenge {ChallengeId} ({ErrorType}); delivery is unconfirmed.", challenge.OtpChallengeId, ex.GetType().Name);
            result = new SmsSendResult(SmsSendOutcome.Unconfirmed);
        }

        var state = result.Outcome switch
        {
            SmsSendOutcome.Accepted => OtpDeliveryState.Accepted,
            SmsSendOutcome.Rejected => OtpDeliveryState.Rejected,
            SmsSendOutcome.Failed => OtpDeliveryState.Failed,
            _ => OtpDeliveryState.Unconfirmed
        };

        challenge.RecordDelivery(state);
        await auditWriter.WriteAsync(callerEmployeeId, action, nameof(OtpChallenge), challenge.OtpChallengeId.ToString(), null,
            $"CrmCustomerId={challenge.CrmCustomerId};CrmUnitId={challenge.CrmUnitId};Channel={challenge.Channel};Delivery={state};Sends={challenge.SendCount}",
            Guid.NewGuid(), cancellationToken);
        if (!await challenges.SaveChangesAsync(cancellationToken))
        {
            logger.LogWarning("Could not record the delivery state of challenge {ChallengeId}.", challenge.OtpChallengeId);
        }

        var masked = Mask(challenge.Destination);
        var resendAt = challenge.LastSentAtUtc.AddSeconds(Math.Max(0, options.ResendCooldownSeconds));
        var sendsLeft = Math.Max(0, Math.Max(1, options.MaxSendsPerChallenge) - challenge.SendCount);

        return result.Outcome switch
        {
            SmsSendOutcome.Accepted => new(OtpStatus.Sent, null, challenge.OtpChallengeId, challenge.Channel, masked,
                challenge.ExpiresAtUtc, resendAt, SendsRemaining: sendsLeft),
            SmsSendOutcome.Rejected => new(OtpStatus.DeliveryRejected, "The SMS provider refused the message. No code reached the customer.",
                challenge.OtpChallengeId, challenge.Channel, masked, Retryable: false),
            SmsSendOutcome.Failed => new(OtpStatus.DeliveryFailed, "The SMS could not be sent. No code reached the customer.",
                challenge.OtpChallengeId, challenge.Channel, masked, ResendAvailableAtUtc: resendAt, Retryable: true),
            _ => new(OtpStatus.DeliveryUnconfirmed,
                "The SMS provider did not give a verifiable answer, so it is unknown whether the SMS was sent. Ask the customer whether a code arrived; the existing code stays valid. Nothing is resent automatically; request a resend only if it did not arrive.",
                challenge.OtpChallengeId, challenge.Channel, masked, challenge.ExpiresAtUtc, resendAt, SendsRemaining: sendsLeft, Retryable: false)
        };
    }

    private async Task<UnitReference> GetOrAddUnitAsync(OtpChallenge challenge, string crmUnitId, DateTime now, CancellationToken cancellationToken)
    {
        var existing = await units.GetByCrmUnitIdAsync(crmUnitId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var unit = new UnitReference(crmUnitId, string.IsNullOrWhiteSpace(challenge.UnitNumber) ? crmUnitId : challenge.UnitNumber, challenge.ProjectName, null, null, now);
        await units.AddAsync(unit, cancellationToken);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return unit;
        }
        catch (DuplicateWriteException)
        {
            return await units.GetByCrmUnitIdAsync(crmUnitId, cancellationToken) ?? throw new InvalidOperationException("Unit reference vanished after a duplicate write.");
        }
    }

    /// <summary>One contact row per (customer, unit): ContactReferences.CrmContactId is globally unique and a contact belongs to one unit.</summary>
    private async Task<ContactReference> GetOrAddContactAsync(
        OtpChallenge challenge, int unitReferenceId, string crmUnitId, DateTime now, CancellationToken cancellationToken)
    {
        var crmContactId = $"buyer:{challenge.CrmCustomerId}:{crmUnitId}";
        var existing = await contacts.GetByCrmContactIdAsync(crmContactId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var contact = new ContactReference(crmContactId, unitReferenceId, null, challenge.Channel, ContactType.Owner, null, now);
        await contacts.AddAsync(contact, cancellationToken);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return contact;
        }
        catch (DuplicateWriteException)
        {
            return await contacts.GetByCrmContactIdAsync(crmContactId, cancellationToken) ?? throw new InvalidOperationException("Contact reference vanished after a duplicate write.");
        }
    }

    // ----------------------------------------------------------------- helpers

    private TimeSpan Lifetime() => TimeSpan.FromMinutes(Math.Max(1, options.CodeLifetimeMinutes));

    private string NewCode()
    {
        var length = Math.Clamp(options.CodeLength, 4, 9);
        var max = 1;
        for (var i = 0; i < length; i++)
        {
            max *= 10;
        }

        return RandomNumberGenerator.GetInt32(0, max).ToString("D" + length, System.Globalization.CultureInfo.InvariantCulture);
    }

    private string Hash(Guid challengeId, string code)
    {
        var key = Encoding.UTF8.GetBytes(options.Pepper);
        var data = Encoding.UTF8.GetBytes($"{challengeId:N}:{code}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, data));
    }

    private string Compose(string language, string code)
    {
        var template = language == "ar" ? options.MessageAr : options.MessageEn;
        return template
            .Replace("{code}", code, StringComparison.Ordinal)
            .Replace("{minutes}", Math.Max(1, options.CodeLifetimeMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private bool TryLanguage(string? requested, out string language, bool allowBlank = false)
    {
        language = null!;
        var value = string.IsNullOrWhiteSpace(requested) ? (allowBlank ? null : options.DefaultLanguage) : requested.Trim();
        if (value is null)
        {
            return true;
        }

        if (string.Equals(value, "en", StringComparison.OrdinalIgnoreCase)) { language = "en"; return true; }
        if (string.Equals(value, "ar", StringComparison.OrdinalIgnoreCase)) { language = "ar"; return true; }
        return false;
    }

    /// <summary>Digits only, international form. A national-form number (single leading 0) is completed only when a country code is configured — never guessed.</summary>
    private string? NormalizeDestination(string? crmMobile)
    {
        var digits = CustomerPhoneNumber.Normalize(crmMobile);
        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }
        else if (digits.StartsWith('0'))
        {
            var cc = CustomerPhoneNumber.Normalize(options.DefaultCountryCode);
            if (cc.Length == 0)
            {
                return null;
            }

            digits = cc + digits[1..];
        }

        return digits.Length is >= 8 and <= 15 && digits.Length <= OtpChallenge.DestinationMaxLength ? digits : null;
    }

    /// <summary>+971*****900: the country prefix and the last three digits only.</summary>
    public static string Mask(string digits) =>
        digits.Length <= 6 ? "***" : $"+{digits[..3]}{new string('*', digits.Length - 6)}{digits[^3..]}";
}
