using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.CrmDocuments.Services;

/// <summary>
/// Real customer verification for a CRM buyer reaching TigerCS through the
/// chatbot: <b>look the buyer up in CRM → pick a unit → email a one-time code
/// to the address CRM holds → check the code on the server → create the
/// OTP-verified session.</b>
///
/// <para>
/// <b>The code is the proof; nothing the caller says is.</b> The phone number
/// the chatbot sends is only the key CRM is searched by. The destination is
/// always the email CRM returns for that customer — no request has a
/// destination field. A session with <c>verificationMethod: Otp</c> can only
/// come out of <see cref="VerifyAsync"/>; the generic session endpoint refuses
/// that method, and the document flow refuses any session that lacks the
/// recorded challenge.
/// </para>
///
/// <para>
/// <b>Bound at creation:</b> the integration account, CRM's customer, the
/// selected unit and its lead. Verification by another account, or for
/// another customer or unit, is impossible — the challenge is looked up by id
/// <i>and</i> account, and the session is built from the challenge's own
/// binding.
/// </para>
///
/// <para>
/// <b>Limits (all configurable, <c>CrmDocuments:Otp*</c>):</b> a code lives
/// 10 minutes; 5 wrong tries lock the challenge for good; at most 3 sends per
/// challenge with 60 s between them (a resend replaces the code); at most 5
/// challenges per customer per hour from any caller; a repeated "send" for a
/// live challenge does not email again. The code is stored only as a salted
/// HMAC and compared in constant time; wrong-try and single-use accounting use
/// concurrency tokens so parallel guesses cannot dodge the budget.
/// </para>
///
/// <para>
/// Disclosure note: the lookup returns the customer's unit labels (project and
/// unit number) <i>before</i> proof, so the customer can choose which unit to
/// verify for — the same information the existing Genesys customer lookup
/// already returns to the call flow. It never returns the customer's name,
/// phone, CRM ids beyond unit/lead, or the full email.
/// </para>
/// </summary>
public sealed class CustomerOtpAppService(
    CrmDocumentOptions options,
    CrmBuyerLookupAppService buyerLookup,
    CrmBuyerVerificationCache cache,
    ICustomerOtpChallengeRepository challenges,
    IUnitReferenceRepository unitRepository,
    IContactReferenceRepository contactRepository,
    VerificationSessionAppService sessionService,
    IOtpCodeGenerator codeGenerator,
    IEmailSender emailSender,
    CustomerNotificationPolicy emailPolicy,
    ICustomerVerificationUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<CustomerOtpAppService> logger)
{
    public const string AuditEntityType = "CustomerOtpChallenge";
    private const int VerifyRetries = 5;

    private TimeSpan Lifetime => TimeSpan.FromMinutes(Math.Max(1, options.OtpLifetimeMinutes));
    private TimeSpan MinResend => TimeSpan.FromSeconds(Math.Max(0, options.OtpMinResendSeconds));
    private int MaxAttempts => Math.Max(1, options.OtpMaxAttempts);
    private int MaxSends => Math.Max(1, options.OtpMaxSendsPerChallenge);

    // ---------------------------------------------------------------- lookup

    /// <summary>Step 1 — find the CRM buyer by phone and list the units they can verify for. Sends nothing, writes nothing.</summary>
    public async Task<CustomerOtpResult> LookupAsync(string? phoneNumber, CancellationToken cancellationToken = default)
    {
        if (Gate() is { } gated)
        {
            return gated;
        }

        if (!TryNormalizePhone(phoneNumber, out var phone))
        {
            return Invalid("phoneNumber must be a phone number.");
        }

        var resolved = await ResolveBuyerAsync(phone, cancellationToken);
        if (resolved.Failure is { } failure)
        {
            return failure;
        }

        var buyer = resolved.Buyer!;
        var units = EligibleUnits(buyer);
        var email = ValidEmail(buyer);

        return new CustomerOtpResult(
            CustomerOtpStatus.Found, Message: units.Count == 0 ? "The customer has no unit that can be verified." : null,
            MaskedDestination: email is null ? null : CrmDocumentCopyAppService.MaskEmail(email),
            Units: units.Select(ToChoice).ToList());
    }

    // ------------------------------------------------------------------ send

    /// <summary>Step 2 — choose the unit and email the code to CRM's address for the customer.</summary>
    public async Task<CustomerOtpResult> SendAsync(
        Guid callerEmployeeId, string? phoneNumber, string? crmUnitId, CancellationToken cancellationToken = default)
    {
        if (Gate() is { } gated)
        {
            return gated;
        }

        if (!TryNormalizePhone(phoneNumber, out var phone))
        {
            return Invalid("phoneNumber must be a phone number.");
        }

        var resolved = await ResolveBuyerAsync(phone, cancellationToken);
        if (resolved.Failure is { } failure)
        {
            return failure;
        }

        var buyer = resolved.Buyer!;
        var email = ValidEmail(buyer);
        if (email is null)
        {
            return Result(CustomerOtpStatus.NoEmailOnRecord, CustomerOtpCodes.NoEmailOnRecord,
                "CRM has no valid email address on record for this customer, so a code cannot be sent. Nothing was sent.");
        }

        // ---- which unit: one of THIS customer's, never a caller-supplied lead ----
        var units = EligibleUnits(buyer);
        CrmBuyerUnitDto selected;
        if (!string.IsNullOrWhiteSpace(crmUnitId))
        {
            var match = units.FirstOrDefault(u => string.Equals(UnitKey(u), crmUnitId.Trim(), StringComparison.Ordinal));
            if (match is null)
            {
                return Result(CustomerOtpStatus.UnitNotOwned, CustomerOtpCodes.UnitNotOwned, "That unit does not belong to this customer.");
            }

            selected = match;
        }
        else if (units.Count == 1)
        {
            selected = units[0];
        }
        else if (units.Count == 0)
        {
            return Result(CustomerOtpStatus.UnitNotOwned, CustomerOtpCodes.UnitNotOwned, "The customer has no unit that can be verified.");
        }
        else
        {
            return new CustomerOtpResult(
                CustomerOtpStatus.UnitSelectionRequired, CustomerOtpCodes.UnitSelectionRequired,
                "The customer has more than one unit. Ask which unit, then call again with its crmUnitId. Nothing was sent.",
                Units: units.Select(ToChoice).ToList());
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var customerId = buyer.Customer.CustomerId;

        // ---- a retried "send" for a live challenge is not a second email ----
        var pending = await challenges.FindPendingAsync(callerEmployeeId, customerId, selected.LeadId, now, cancellationToken);
        if (pending is not null)
        {
            return await ResendCoreAsync(pending, email, selected, alreadySentStatus: CustomerOtpStatus.AlreadySent, cancellationToken);
        }

        // ---- cap on how many emails anyone can make us send this customer ----
        var recent = await challenges.CountStartedSinceAsync(customerId, now.AddHours(-1), cancellationToken);
        if (recent >= Math.Max(1, options.OtpMaxChallengesPerCustomerPerHour))
        {
            return new CustomerOtpResult(
                CustomerOtpStatus.RateLimited, CustomerOtpCodes.OtpRateLimited,
                "Too many verification codes were requested for this customer. Try again later. Nothing was sent.",
                RetryAfterSeconds: 3600);
        }

        // ---- cache the buyer's unit + unit-scoped contact, then bind the challenge to them ----
        UnitReference unit;
        ContactReference contact;
        try
        {
            (unit, contact) = await cache.EnsureAsync(buyer, selected, phone, cancellationToken);
        }
        catch (BuyerCacheConflictException ex)
        {
            logger.LogError(ex, "Buyer verification cache conflict.");
            return Result(CustomerOtpStatus.CrmInvalidResponse, CustomerOtpCodes.CrmInvalidResponse,
                "CRM data could not be mapped to a single unit and contact. Nothing was sent.");
        }

        var code = codeGenerator.NewCode();
        var challengeId = Guid.NewGuid();
        var salt = RandomNumberGenerator.GetBytes(16);
        var masked = CrmDocumentCopyAppService.MaskEmail(email);
        var challenge = new CustomerOtpChallenge(
            challengeId, callerEmployeeId, customerId, selected.LeadId, unit.UnitReferenceId, contact.ContactReferenceId,
            masked, salt, HashCode(challengeId, salt, code), now, Lifetime);

        await challenges.AddAsync(challenge, cancellationToken);
        await auditWriter.WriteAsync(
            callerEmployeeId, "CustomerOtpSent", AuditEntityType, challengeId.ToString(), beforeValue: null,
            afterValue: $"CrmCustomerId={customerId};CrmLeadId={selected.LeadId};Destination={masked};Send=1",
            Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var delivery = await DeliverAsync(email, code, challenge, cancellationToken);
        if (delivery is not null)
        {
            return delivery;
        }

        return Sent(challenge, CustomerOtpStatus.CodeSent, unit, selected);
    }

    // ---------------------------------------------------------------- resend

    public async Task<CustomerOtpResult> ResendAsync(Guid callerEmployeeId, Guid? challengeId, CancellationToken cancellationToken = default)
    {
        if (Gate() is { } gated)
        {
            return gated;
        }

        if (challengeId is not { } id || id == Guid.Empty)
        {
            return Invalid("challengeId is required.");
        }

        var challenge = await challenges.GetByIdAsync(id, cancellationToken);
        if (challenge is null || challenge.CallerEmployeeId != callerEmployeeId)
        {
            return NotFound();
        }

        // The destination is re-read from CRM through the contact the challenge was bound to — not stored, not supplied.
        var contact = await contactRepository.GetByIdAsync(challenge.ContactReferenceId, cancellationToken);
        if (contact is null || !TryNormalizePhone(contact.ContactChannel, out var phone))
        {
            return Result(CustomerOtpStatus.CustomerNotFound, CustomerOtpCodes.CustomerNotFound, "The customer can no longer be resolved. Start again.");
        }

        var resolved = await ResolveBuyerAsync(phone, cancellationToken);
        if (resolved.Failure is { } failure)
        {
            return failure;
        }

        var buyer = resolved.Buyer!;
        var email = ValidEmail(buyer);
        var lead = buyer.Customer.CustomerId == challenge.CrmCustomerId
            ? EligibleUnits(buyer).FirstOrDefault(u => u.LeadId == challenge.CrmLeadId)
            : null;
        if (lead is null)
        {
            return Result(CustomerOtpStatus.UnitNotOwned, CustomerOtpCodes.UnitNotOwned, "The challenge no longer matches this customer's units. Start again.");
        }

        if (email is null)
        {
            return Result(CustomerOtpStatus.NoEmailOnRecord, CustomerOtpCodes.NoEmailOnRecord, "CRM has no valid email address on record for this customer. Nothing was sent.");
        }

        return await ResendCoreAsync(challenge, email, lead, alreadySentStatus: null, cancellationToken);
    }

    private async Task<CustomerOtpResult> ResendCoreAsync(
        CustomerOtpChallenge challenge, string email, CrmBuyerUnitDto lead, CustomerOtpStatus? alreadySentStatus, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (challenge.Status != OtpChallengeStatus.Pending || challenge.IsExpired(now))
        {
            return challenge.Status switch
            {
                OtpChallengeStatus.Locked => Result(CustomerOtpStatus.Locked, CustomerOtpCodes.OtpLocked, "This challenge is locked. Start again."),
                OtpChallengeStatus.Verified => Result(CustomerOtpStatus.AlreadyUsed, CustomerOtpCodes.OtpAlreadyUsed, "This challenge was already used."),
                _ => Result(CustomerOtpStatus.Expired, CustomerOtpCodes.OtpExpired, "This challenge has expired. Start again.")
            };
        }

        if (challenge.SendCount >= MaxSends)
        {
            return Result(CustomerOtpStatus.ResendLimitReached, CustomerOtpCodes.OtpResendLimit,
                "The number of codes allowed for this challenge has been used. Start again later.");
        }

        if (now < challenge.NextResendAllowedAtUtc(MinResend))
        {
            var wait = (int)Math.Ceiling((challenge.NextResendAllowedAtUtc(MinResend) - now).TotalSeconds);
            // A retried "send" inside the interval is answered with the live challenge — nothing is emailed again.
            return alreadySentStatus is { } already
                ? Sent(challenge, already, unit: null, lead) with { Message = "A code was already sent for this customer and unit. Nothing was sent again.", RetryAfterSeconds = wait }
                : new CustomerOtpResult(
                    CustomerOtpStatus.ResendTooSoon, CustomerOtpCodes.OtpResendTooSoon,
                    $"A code was sent moments ago. Wait {wait} seconds before asking for another.",
                    ChallengeId: challenge.CustomerOtpChallengeId, MaskedDestination: challenge.MaskedDestination,
                    ResendAvailableAtUtc: challenge.NextResendAllowedAtUtc(MinResend), RetryAfterSeconds: wait);
        }

        var code = codeGenerator.NewCode();
        var salt = RandomNumberGenerator.GetBytes(16);
        challenge.RecordResend(salt, HashCode(challenge.CustomerOtpChallengeId, salt, code), now, Lifetime);
        await auditWriter.WriteAsync(
            challenge.CallerEmployeeId, "CustomerOtpResent", AuditEntityType, challenge.CustomerOtpChallengeId.ToString(),
            beforeValue: null, afterValue: $"Send={challenge.SendCount}", Guid.NewGuid(), cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrentWriteException)
        {
            unitOfWork.DiscardPendingChanges();
            // A parallel resend/verify won; the code we generated was never stored, so none is emailed.
            return Result(CustomerOtpStatus.ResendTooSoon, CustomerOtpCodes.OtpResendTooSoon, "Another request changed this challenge. Try again.");
        }

        var delivery = await DeliverAsync(email, code, challenge, cancellationToken);
        return delivery ?? Sent(challenge, CustomerOtpStatus.CodeSent, unit: null, lead);
    }

    // ---------------------------------------------------------------- verify

    /// <summary>Step 3 — check the code; on success create the OTP-verified session, atomically with spending the challenge.</summary>
    public async Task<CustomerOtpResult> VerifyAsync(
        Guid callerEmployeeId, Guid? challengeId, string? code, CancellationToken cancellationToken = default)
    {
        if (Gate() is { } gated)
        {
            return gated;
        }

        if (challengeId is not { } id || id == Guid.Empty || string.IsNullOrWhiteSpace(code))
        {
            return Invalid("challengeId and code are required.");
        }

        var submitted = code.Trim();

        for (var attempt = 0; attempt < VerifyRetries; attempt++)
        {
            var challenge = await challenges.GetByIdAsync(id, cancellationToken);
            if (challenge is null || challenge.CallerEmployeeId != callerEmployeeId)
            {
                return NotFound();
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var matches = CodeMatches(challenge, submitted);
            var outcome = challenge.Verify(matches, now, MaxAttempts);

            VerificationSession? session = null;
            if (outcome == OtpVerifyOutcome.Verified)
            {
                var unit = await unitRepository.GetByIdAsync(challenge.UnitReferenceId, cancellationToken);
                var contact = await contactRepository.GetByIdAsync(challenge.ContactReferenceId, cancellationToken);
                if (unit is null || contact is null)
                {
                    return Result(CustomerOtpStatus.CrmInvalidResponse, CustomerOtpCodes.CrmInvalidResponse, "The verified unit is no longer on record. Start again.");
                }

                session = await sessionService.StageOtpVerifiedSessionAsync(callerEmployeeId, unit, contact, challenge, cancellationToken);
                challenge.LinkSession(session.VerificationSessionId);
            }

            if (outcome is OtpVerifyOutcome.WrongCode or OtpVerifyOutcome.Locked)
            {
                await auditWriter.WriteAsync(
                    callerEmployeeId, outcome == OtpVerifyOutcome.Locked ? "CustomerOtpLocked" : "CustomerOtpWrongCode",
                    AuditEntityType, challenge.CustomerOtpChallengeId.ToString(), beforeValue: null,
                    afterValue: $"FailedAttempts={challenge.FailedAttempts}", Guid.NewGuid(), cancellationToken);
            }
            else if (outcome == OtpVerifyOutcome.Verified)
            {
                await auditWriter.WriteAsync(
                    callerEmployeeId, "CustomerOtpVerified", AuditEntityType, challenge.CustomerOtpChallengeId.ToString(), beforeValue: null,
                    afterValue: $"CrmCustomerId={challenge.CrmCustomerId};CrmLeadId={challenge.CrmLeadId};VerificationSessionId={session!.VerificationSessionId}",
                    Guid.NewGuid(), cancellationToken);
            }

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (ConcurrentWriteException)
            {
                // Someone changed the challenge between our read and write (a parallel guess or the same
                // correct code twice). Nothing of ours was saved. Drop our staged changes (the mutated challenge,
                // the staged session and audit rows) and start over from what is really stored, so every
                // attempt is counted and a code is spent exactly once.
                unitOfWork.DiscardPendingChanges();
                continue;
            }

            return outcome switch
            {
                OtpVerifyOutcome.Verified => new CustomerOtpResult(
                    CustomerOtpStatus.Verified, Message: "The customer is verified.",
                    ChallengeId: challenge.CustomerOtpChallengeId, Session: sessionService.ToResponse(session!)),
                OtpVerifyOutcome.WrongCode => new CustomerOtpResult(
                    CustomerOtpStatus.InvalidCode, CustomerOtpCodes.OtpInvalid, "That code is not correct.",
                    ChallengeId: challenge.CustomerOtpChallengeId, AttemptsRemaining: Math.Max(0, MaxAttempts - challenge.FailedAttempts)),
                OtpVerifyOutcome.Locked => Result(CustomerOtpStatus.Locked, CustomerOtpCodes.OtpLocked, "Too many wrong codes. This challenge is locked; start again."),
                OtpVerifyOutcome.Expired => Result(CustomerOtpStatus.Expired, CustomerOtpCodes.OtpExpired, "The code has expired. Ask for a new one."),
                _ => Result(CustomerOtpStatus.AlreadyUsed, CustomerOtpCodes.OtpAlreadyUsed, "This code was already used.")
            };
        }

        return Result(CustomerOtpStatus.InvalidCode, CustomerOtpCodes.OtpInvalid, "Too many simultaneous attempts. Try again.");
    }

    // --------------------------------------------------------------- helpers

    private CustomerOtpResult? Gate() =>
        options.Enabled
            ? null
            : Result(CustomerOtpStatus.Disabled, CustomerOtpCodes.Disabled, "Customer document verification is switched off (CrmDocuments:Enabled).");

    private static CustomerOtpResult Invalid(string message) => Result(CustomerOtpStatus.InvalidRequest, CustomerOtpCodes.InvalidRequest, message);

    private static CustomerOtpResult NotFound() =>
        Result(CustomerOtpStatus.ChallengeNotFound, CustomerOtpCodes.ChallengeNotFound, "No such verification challenge for this caller.");

    private static CustomerOtpResult Result(CustomerOtpStatus status, string code, string message) => new(status, code, message);

    private CustomerOtpResult Sent(CustomerOtpChallenge c, CustomerOtpStatus status, UnitReference? unit, CrmBuyerUnitDto lead) => new(
        status, Message: status == CustomerOtpStatus.AlreadySent ? "A code was already sent." : "A code was emailed to the address CRM holds for this customer.",
        ChallengeId: c.CustomerOtpChallengeId, MaskedDestination: c.MaskedDestination, ExpiresAtUtc: c.ExpiresAtUtc,
        ResendAvailableAtUtc: c.SendCount < MaxSends ? c.NextResendAllowedAtUtc(MinResend) : null,
        AttemptsRemaining: Math.Max(0, MaxAttempts - c.FailedAttempts), Unit: ToChoice(lead));

    private async Task<CustomerOtpResult?> DeliverAsync(string email, string code, CustomerOtpChallenge challenge, CancellationToken cancellationToken)
    {
        if (!emailPolicy.Enabled)
        {
            return Result(CustomerOtpStatus.DeliveryFailed, CustomerOtpCodes.DeliveryFailed,
                "Email delivery is switched off (EmailNotifications:Enabled), so no code could be sent.");
        }

        var minutes = (int)Lifetime.TotalMinutes;
        var body = $"Your Tiger Properties verification code is {code}.\r\n\r\nIt is valid for {minutes} minutes. "
            + "Do not share it with anyone. If you did not ask for it, you can ignore this email.\r\n\r\nTiger Properties";

        EmailSendResult sent;
        try
        {
            sent = await emailSender.SendAsync(
                new EmailMessage(email, "Your Tiger Properties verification code", body, challenge.CustomerOtpChallengeId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Sending the verification code threw.");
            sent = EmailSendResult.Transient("exception");
        }

        return sent.Outcome == EmailSendOutcome.Sent
            ? null
            : Result(CustomerOtpStatus.DeliveryFailed, CustomerOtpCodes.DeliveryFailed,
                "The verification code could not be emailed. Try again shortly.");
    }

    private async Task<(CrmBuyerMatchDto? Buyer, CustomerOtpResult? Failure)> ResolveBuyerAsync(string phone, CancellationToken cancellationToken)
    {
        var lookup = await buyerLookup.GetBuyerByPhoneAsync(phone, cancellationToken);
        return lookup.Outcome switch
        {
            CrmBuyerLookupOutcome.Success when lookup.Buyers is { Count: 1 } => (lookup.Buyers[0], null),
            CrmBuyerLookupOutcome.Success or CrmBuyerLookupOutcome.AmbiguousCustomerMatch =>
                (null, Result(CustomerOtpStatus.CustomerAmbiguous, CustomerOtpCodes.CustomerAmbiguous,
                    "CRM returned more than one customer for this number, so no one can be verified through the chatbot. Use manual verification.")),
            CrmBuyerLookupOutcome.NotFound =>
                (null, Result(CustomerOtpStatus.CustomerNotFound, CustomerOtpCodes.CustomerNotFound, "No CRM buyer was found for this number.")),
            CrmBuyerLookupOutcome.Unauthorized =>
                (null, Result(CustomerOtpStatus.CrmAuthenticationFailed, CustomerOtpCodes.CrmAuthenticationFailed, "CRM rejected TigerCS's credential. Nothing was sent.")),
            CrmBuyerLookupOutcome.InvalidResponse =>
                (null, Result(CustomerOtpStatus.CrmInvalidResponse, CustomerOtpCodes.CrmInvalidResponse, "CRM returned an unusable answer. Nothing was sent.")),
            _ => (null, Result(CustomerOtpStatus.CrmUnavailable, CustomerOtpCodes.CrmUnavailable, "CRM is unavailable right now. Nothing was sent."))
        };
    }

    /// <summary>Units that can be cached and verified for: they must have a unit number.</summary>
    private static List<CrmBuyerUnitDto> EligibleUnits(CrmBuyerMatchDto buyer) =>
        buyer.Units.Where(u => !string.IsNullOrWhiteSpace(u.UnitNumber)).DistinctBy(u => u.UnitId).OrderBy(u => u.UnitId).ToList();

    private static string UnitKey(CrmBuyerUnitDto unit) => unit.UnitId.ToString(CultureInfo.InvariantCulture);

    private static BuyerUnitChoice ToChoice(CrmBuyerUnitDto unit) =>
        new(UnitKey(unit), unit.LeadId, unit.UnitNumber!.Trim(), string.IsNullOrWhiteSpace(unit.ProjectName) ? null : unit.ProjectName.Trim());

    private static string? ValidEmail(CrmBuyerMatchDto buyer)
    {
        var email = buyer.Customer.Email?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 254)
        {
            return null;
        }

        var at = email.IndexOf('@');
        return at > 0 && at == email.LastIndexOf('@') && email.IndexOf('.', at) > at + 1 && !email.Any(char.IsWhiteSpace) ? email : null;
    }

    private static bool TryNormalizePhone(string? value, out string phone)
    {
        phone = string.Empty;
        if (!CustomerPhoneNumber.LooksLikeNumber(value))
        {
            return false;
        }

        var digits = CustomerPhoneNumber.Normalize(value);
        if (digits.Length is < 7 or > 15)
        {
            return false;
        }

        phone = "+" + digits;
        return true;
    }

    // ---- code hashing: salted HMAC, constant-time compare ----

    private byte[] HashCode(Guid challengeId, byte[] salt, string code)
    {
        var key = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(options.OtpCodePepper) ? "tigercs-customer-otp" : options.OtpCodePepper);
        using var hmac = new HMACSHA256(key);
        var payload = new List<byte>(challengeId.ToByteArray());
        payload.AddRange(salt);
        payload.AddRange(Encoding.UTF8.GetBytes(code));
        return hmac.ComputeHash(payload.ToArray());
    }

    private bool CodeMatches(CustomerOtpChallenge challenge, string submitted)
    {
        // Always do the hash and the comparison, whatever the shape of the guess, so timing does not vary.
        var expected = challenge.CodeHash;
        var actual = HashCode(challenge.CustomerOtpChallengeId, challenge.Salt, submitted);
        var equal = CryptographicOperations.FixedTimeEquals(expected, actual);
        return equal && submitted.Length == 6 && submitted.All(char.IsAsciiDigit);
    }
}
