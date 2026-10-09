namespace TigerCS.Domain.Modules.CustomerVerification;

public enum OtpChallengeStatus
{
    /// <summary>A code was issued and may still be verified (until it expires or attempts run out).</summary>
    Pending = 1,

    /// <summary>The right code was entered; <see cref="OtpChallenge.VerificationSessionId"/> holds the confirmed session.</summary>
    Verified = 2,

    /// <summary>Too many wrong codes. Terminal.</summary>
    Locked = 3,

    /// <summary>The provider refused or failed the send, so no code reached the customer. Terminal.</summary>
    DeliveryFailed = 4
}

/// <summary>What is known about the SMS that carried the current code.</summary>
public enum OtpDeliveryState
{
    /// <summary>Recorded before the provider is called; also the state if the process dies mid-send.</summary>
    NotSent = 0,

    /// <summary>The provider's response was verified as acceptance. (Handset delivery is not confirmed; the provider gives no receipt here.)</summary>
    Accepted = 1,

    /// <summary>The provider refused the message.</summary>
    Rejected = 2,

    /// <summary>The provider could not be reached or errored; the message was not accepted.</summary>
    Failed = 3,

    /// <summary>Timeout or an unreadable response: the SMS may or may not have been sent. Never retried automatically.</summary>
    Unconfirmed = 4
}

/// <summary>
/// One-time-code challenge for a customer's unit. <b>Only a keyed hash of the code is
/// stored</b> (HMAC-SHA-256 over the challenge id and code, keyed by the server-side
/// pepper); the code itself exists only in the SMS. A successful verification does
/// not create a new notion of "verified": it records an ordinary
/// <see cref="VerificationSession"/> (method <see cref="VerificationMethod.Otp"/>) —
/// the same owned, unit-bound, expiring proof every other flow already uses.
/// </summary>
public class OtpChallenge
{
    public const int CodeHashLength = 64;
    public const int DestinationMaxLength = 32;
    public const int ChannelMaxLength = 16;

    public Guid OtpChallengeId { get; private set; }

    /// <summary>The authenticated TigerCS caller (the Genesys service account) that issued it; only that caller may resend or verify it.</summary>
    public Guid OwnerEmployeeId { get; private set; }

    public int CrmCustomerId { get; private set; }
    public int CrmUnitId { get; private set; }
    public string? UnitNumber { get; private set; }
    public string? ProjectName { get; private set; }

    public string Channel { get; private set; } = string.Empty;

    /// <summary>The customer's mobile as CRM holds it — never a number the caller supplied.</summary>
    public string Destination { get; private set; } = string.Empty;

    public string Language { get; private set; } = "en";

    /// <summary>Lower-case hex HMAC-SHA-256 of the current code.</summary>
    public string CodeHash { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime LastSentAtUtc { get; private set; }
    public int SendCount { get; private set; }
    public int FailedAttempts { get; private set; }
    public OtpChallengeStatus Status { get; private set; }
    public OtpDeliveryState DeliveryState { get; private set; }
    public Guid? VerificationSessionId { get; private set; }

    /// <summary>Optimistic-concurrency token: bumped on every change so two simultaneous verifies/resends cannot both win.</summary>
    public int Version { get; private set; }

    private OtpChallenge() { }

    public OtpChallenge(
        Guid otpChallengeId, Guid ownerEmployeeId, int crmCustomerId, int crmUnitId, string? unitNumber, string? projectName,
        string channel, string destination, string language, string codeHash, DateTime nowUtc, TimeSpan lifetime)
    {
        OtpChallengeId = otpChallengeId;
        OwnerEmployeeId = ownerEmployeeId;
        CrmCustomerId = crmCustomerId;
        CrmUnitId = crmUnitId;
        UnitNumber = unitNumber;
        ProjectName = projectName;
        Channel = channel;
        Destination = destination;
        Language = language;
        CodeHash = codeHash;
        CreatedAtUtc = nowUtc;
        LastSentAtUtc = nowUtc;
        ExpiresAtUtc = nowUtc + lifetime;
        SendCount = 1;
        Status = OtpChallengeStatus.Pending;
        DeliveryState = OtpDeliveryState.NotSent;
    }

    public bool IsOwnedBy(Guid employeeId) => OwnerEmployeeId == employeeId;

    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;

    /// <summary>A resend replaces the code: the old one stops working, the clock and the attempt counter restart.</summary>
    public void ReplaceCode(string codeHash, DateTime nowUtc, TimeSpan lifetime, string? language = null)
    {
        if (language is not null)
        {
            Language = language;
        }

        CodeHash = codeHash;
        LastSentAtUtc = nowUtc;
        ExpiresAtUtc = nowUtc + lifetime;
        SendCount++;
        FailedAttempts = 0;
        DeliveryState = OtpDeliveryState.NotSent;
        Status = OtpChallengeStatus.Pending;
        Version++;
    }

    public void RecordDelivery(OtpDeliveryState state)
    {
        DeliveryState = state;
        if (state is OtpDeliveryState.Rejected or OtpDeliveryState.Failed)
        {
            Status = OtpChallengeStatus.DeliveryFailed;
        }

        Version++;
    }

    /// <summary>Counts a wrong code; locks the challenge when <paramref name="maxAttempts"/> is reached. Returns the attempts left.</summary>
    public int RegisterWrongCode(int maxAttempts)
    {
        FailedAttempts++;
        if (FailedAttempts >= maxAttempts)
        {
            Status = OtpChallengeStatus.Locked;
        }

        Version++;
        return Math.Max(0, maxAttempts - FailedAttempts);
    }

    public void MarkVerified(Guid verificationSessionId)
    {
        Status = OtpChallengeStatus.Verified;
        VerificationSessionId = verificationSessionId;
        Version++;
    }
}
