namespace TigerCS.Domain.Modules.CustomerVerification;

public enum OtpChallengeStatus : byte
{
    /// <summary>A code has been sent and may still be entered.</summary>
    Pending = 1,

    /// <summary>The correct code was entered; the challenge is spent (single use) and a session was created from it.</summary>
    Verified = 2,

    /// <summary>Too many wrong codes. Final — a new challenge is needed.</summary>
    Locked = 3
}

/// <summary>How the code reaches the customer. Both channels share one challenge model, one verification path and one resulting session.</summary>
public enum OtpChannel : byte
{
    Email = 1,
    Sms = 2
}

/// <summary>What is known about the message that carried the current code.</summary>
public enum OtpDeliveryState : byte
{
    /// <summary>Recorded before the provider is called; also what remains if the process dies mid-send.</summary>
    NotSent = 0,

    /// <summary>The provider's response was verified as acceptance (an SMS gateway's acceptance is not handset delivery).</summary>
    Accepted = 1,

    /// <summary>The provider refused the message.</summary>
    Rejected = 2,

    /// <summary>The message was not accepted (provider unreachable or erroring).</summary>
    Failed = 3,

    /// <summary>Timeout or unreadable answer: the message may or may not have been sent. Never retried automatically.</summary>
    Unconfirmed = 4
}

public enum OtpVerifyOutcome
{
    Verified,
    WrongCode,
    Expired,
    Locked,
    AlreadyUsed
}

/// <summary>
/// One server-issued email one-time-code challenge for a CRM buyer: the proof
/// behind an OTP-verified session.
///
/// <para>
/// <b>Bound at creation, never rebound:</b> the requesting integration account
/// (<see cref="CallerEmployeeId"/>), CRM's customer and lead
/// (<see cref="CrmCustomerId"/>, <see cref="CrmLeadId"/>), the cached unit and
/// contact, and the masked destination. The code goes to the email CRM returned
/// for that customer — a destination is never a field of any request.
/// </para>
///
/// <para>
/// <b>The code is never stored.</b> Only a salted HMAC (<see cref="CodeHash"/>)
/// is; it is compared in constant time. Expiry, a wrong-attempt budget, a resend
/// budget with a minimum interval, and single use are all enforced here, in the
/// domain, and <see cref="Status"/>/<see cref="FailedAttempts"/>/<see cref="SendCount"/> are
/// concurrency tokens so parallel guesses cannot dodge the budget or spend a
/// code twice.
/// </para>
/// </summary>
public class CustomerOtpChallenge
{
    public const int MaskedDestinationMaxLength = 120;

    public Guid CustomerOtpChallengeId { get; private set; }
    public Guid CallerEmployeeId { get; private set; }
    public int CrmCustomerId { get; private set; }
    public int CrmLeadId { get; private set; }
    public int UnitReferenceId { get; private set; }
    public int ContactReferenceId { get; private set; }
    public string MaskedDestination { get; private set; } = string.Empty;

    /// <summary>Email (default, and every row created before SMS existed) or Sms.</summary>
    public OtpChannel Channel { get; private set; } = OtpChannel.Email;

    /// <summary>Language of the message ("en"/"ar"); only SMS uses it.</summary>
    public string Language { get; private set; } = "en";

    /// <summary>What is known about the current code's message. Not a concurrency token: recording it never competes with a verify.</summary>
    public OtpDeliveryState DeliveryState { get; private set; }

    public byte[] Salt { get; private set; } = [];
    public byte[] CodeHash { get; private set; } = [];

    public OtpChallengeStatus Status { get; private set; }
    public int FailedAttempts { get; private set; }
    public int SendCount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime LastSentAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? VerifiedAtUtc { get; private set; }

    /// <summary>The session created from this challenge, once verified.</summary>
    public Guid? VerificationSessionId { get; private set; }

    private CustomerOtpChallenge() { }

    public CustomerOtpChallenge(
        Guid challengeId, Guid callerEmployeeId, int crmCustomerId, int crmLeadId, int unitReferenceId, int contactReferenceId,
        string maskedDestination, byte[] salt, byte[] codeHash, DateTime nowUtc, TimeSpan lifetime,
        OtpChannel channel = OtpChannel.Email, string language = "en")
    {
        if (callerEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("CallerEmployeeId is required.", nameof(callerEmployeeId));
        }

        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        CustomerOtpChallengeId = challengeId;
        CallerEmployeeId = callerEmployeeId;
        CrmCustomerId = crmCustomerId;
        CrmLeadId = crmLeadId;
        UnitReferenceId = unitReferenceId;
        ContactReferenceId = contactReferenceId;
        MaskedDestination = maskedDestination.Length <= MaskedDestinationMaxLength ? maskedDestination : maskedDestination[..MaskedDestinationMaxLength];
        Channel = channel;
        Language = language;
        Salt = salt;
        CodeHash = codeHash;
        Status = OtpChallengeStatus.Pending;
        SendCount = 1;
        CreatedAtUtc = nowUtc;
        LastSentAtUtc = nowUtc;
        ExpiresAtUtc = nowUtc + lifetime;
    }

    public bool IsExpired(DateTime nowUtc) => nowUtc > ExpiresAtUtc;

    /// <summary>When a resend becomes allowed.</summary>
    public DateTime NextResendAllowedAtUtc(TimeSpan minInterval) => LastSentAtUtc + minInterval;

    public bool CanResend(DateTime nowUtc, TimeSpan minInterval, int maxSends) =>
        Status == OtpChallengeStatus.Pending && !IsExpired(nowUtc) && SendCount < maxSends && nowUtc >= NextResendAllowedAtUtc(minInterval);

    /// <summary>
    /// A resend replaces the code: the previous one stops working, the expiry
    /// restarts, and the wrong-attempt count resets to zero — otherwise a
    /// customer who mistyped twice and asked again would start short.
    /// The send budget (<see cref="SendCount"/>) is never reset.
    /// </summary>
    public void RecordResend(byte[] salt, byte[] codeHash, DateTime nowUtc, TimeSpan lifetime, string? language = null)
    {
        if (language is not null)
        {
            Language = language;
        }

        DeliveryState = OtpDeliveryState.NotSent;
        Salt = salt;
        CodeHash = codeHash;
        SendCount++;
        LastSentAtUtc = nowUtc;
        ExpiresAtUtc = nowUtc + lifetime;
        FailedAttempts = 0;
    }

    /// <summary>
    /// Checks a submitted code. <paramref name="codeMatches"/> is the
    /// constant-time comparison result, computed by the caller (which owns the
    /// HMAC key); everything stateful happens here: a wrong code spends an
    /// attempt (locking at <paramref name="maxAttempts"/>), the right one spends
    /// the challenge for good.
    /// </summary>
    public OtpVerifyOutcome Verify(bool codeMatches, DateTime nowUtc, int maxAttempts)
    {
        if (Status == OtpChallengeStatus.Verified)
        {
            return OtpVerifyOutcome.AlreadyUsed;
        }

        if (Status == OtpChallengeStatus.Locked)
        {
            return OtpVerifyOutcome.Locked;
        }

        if (IsExpired(nowUtc))
        {
            return OtpVerifyOutcome.Expired;
        }

        if (!codeMatches)
        {
            FailedAttempts++;
            if (FailedAttempts >= maxAttempts)
            {
                Status = OtpChallengeStatus.Locked;
                return OtpVerifyOutcome.Locked;
            }

            return OtpVerifyOutcome.WrongCode;
        }

        Status = OtpChallengeStatus.Verified;
        VerifiedAtUtc = nowUtc;
        return OtpVerifyOutcome.Verified;
    }

    public void LinkSession(Guid verificationSessionId) => VerificationSessionId = verificationSessionId;

    public void RecordDelivery(OtpDeliveryState state) => DeliveryState = state;
}
