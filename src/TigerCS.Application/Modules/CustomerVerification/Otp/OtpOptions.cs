namespace TigerCS.Application.Modules.CustomerVerification.Otp;

/// <summary>
/// <c>Otp</c> configuration. None of these existed before the OTP flow was added to
/// TigerCS, so every value below is a <b>proposed default</b>, not an inherited one;
/// change them in configuration, not code. The verification-session lifetime that follows
/// a successful check is the existing <c>VerificationSessionAppService.SessionLifetime</c>.
/// </summary>
public sealed class OtpOptions
{
    public const string SectionName = "Otp";

    /// <summary>Master switch. Off by default; nothing is issued or sent while false.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Server-side secret mixed into the code hash (HMAC key). Required to issue or verify a code.
    /// Never commit a value: supply it as an environment variable (<c>Otp__Pepper</c>) or a secret store.
    /// Changing it invalidates every outstanding code.
    /// </summary>
    public string Pepper { get; set; } = string.Empty;

    public int CodeLength { get; set; } = 6;

    public int CodeLifetimeMinutes { get; set; } = 5;

    /// <summary>Wrong codes allowed per issued code before the challenge locks.</summary>
    public int MaxVerifyAttempts { get; set; } = 5;

    /// <summary>Codes (first send + resends) per challenge.</summary>
    public int MaxSendsPerChallenge { get; set; } = 3;

    public int ResendCooldownSeconds { get; set; } = 60;

    /// <summary>New challenges per CRM customer per rolling hour (stops SMS flooding of one customer).</summary>
    public int MaxChallengesPerCustomerPerHour { get; set; } = 5;

    /// <summary>A challenge older than this cannot be resent or verified; start a new one. Matches the session lifetime that follows a successful check.</summary>
    public int MaxChallengeAgeMinutes { get; set; } = 30;

    /// <summary>
    /// Country calling code (digits, e.g. the UAE's) prepended to a CRM mobile written in national
    /// form with a single leading 0. Empty (default) = such numbers are refused rather than guessed.
    /// </summary>
    public string DefaultCountryCode { get; set; } = string.Empty;

    /// <summary>"en" or "ar": used when the request names no language.</summary>
    public string DefaultLanguage { get; set; } = "en";

    /// <summary>Placeholders: {code}, {minutes}.</summary>
    public string MessageEn { get; set; } = "Your Tiger verification code is {code}. It expires in {minutes} minutes. Do not share it with anyone.";

    public string MessageAr { get; set; } = "رمز التحقق الخاص بك من تايجر هو {code}. تنتهي صلاحيته خلال {minutes} دقائق. لا تشاركه مع أحد.";

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Pepper);
}
