using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CrmDocuments;

/// <summary>
/// Settings for the chatbot's "send me a copy of my document" API, bound from
/// <c>CrmDocuments</c>. A plain Application-layer value (the composition root
/// binds it), like <c>GenesysOptions</c>.
/// </summary>
public sealed class CrmDocumentOptions
{
    public const string SectionName = "CrmDocuments";

    /// <summary>The feature flag. False (the default) answers 503 <c>DOCUMENT_COPY_DISABLED</c> and touches nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Verification methods accepted as proof of identity for a chatbot
    /// document request. Default: a one-time code, or an authenticated digital
    /// user. <c>ManualAgentConfirmation</c> is deliberately absent — an agent
    /// ticking a box is not something a bot channel should lean on to release
    /// contracts — and so is any method a caller could assert on its own.
    /// </summary>
    public List<string> AcceptedVerificationMethods { get; set; } =
        [nameof(VerificationMethod.Otp), nameof(VerificationMethod.AuthenticatedDigitalUser)];

    /// <summary>Largest document sent as an attachment. Bigger answers DELIVERY_FAILED/DOCUMENT_TOO_LARGE rather than risking a mailbox rejection.</summary>
    public int MaxAttachmentBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>An unfinished send older than this is treated as abandoned and may be retaken by a retry.</summary>
    public int InProgressStaleAfterMinutes { get; set; } = 5;

    /// <summary>The same document, session and channel is not sent again within this window even under a new idempotency key.</summary>
    public int DuplicateSuppressionMinutes { get; set; } = 15;

    /// <summary>
    /// Production guard. The chatbot document flow ships disabled and, in a
    /// Production environment, refuses to start enabled unless this is set too:
    /// turning it on there is a deliberate act made only after real UAT
    /// verification passes (see docs/Genesys/Document-Copy-API.md §Status).
    /// </summary>
    public bool AllowInProduction { get; set; }

    // ---- email one-time-code verification (CustomerOtpAppService) ----

    /// <summary>How long a code is valid. Resending restarts it.</summary>
    public int OtpLifetimeMinutes { get; set; } = 10;

    /// <summary>Wrong codes allowed per code before the challenge locks for good.</summary>
    public int OtpMaxAttempts { get; set; } = 5;

    /// <summary>Codes sent per challenge, including the first.</summary>
    public int OtpMaxSendsPerChallenge { get; set; } = 3;

    /// <summary>Minimum seconds between two sends of one challenge.</summary>
    public int OtpMinResendSeconds { get; set; } = 60;

    /// <summary>Challenges started per CRM customer per hour, across every caller — the cap on how many emails anyone can make us send a customer.</summary>
    public int OtpMaxChallengesPerCustomerPerHour { get; set; } = 5;

    /// <summary>Server-side secret mixed into the stored code hash. Set it in configuration (user-secrets / environment), never commit it.</summary>
    public string? OtpCodePepper { get; set; }

    public bool IsAccepted(VerificationMethod? method) =>
        method is { } m && AcceptedVerificationMethods.Contains(m.ToString(), StringComparer.Ordinal);
}
