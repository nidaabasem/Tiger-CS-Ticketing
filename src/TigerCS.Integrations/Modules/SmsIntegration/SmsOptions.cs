namespace TigerCS.Integrations.Modules.SmsIntegration;

/// <summary>
/// <c>Sms</c> configuration. <b>No provider value is defaulted</b>: endpoint, credentials, sender id,
/// the English/Arabic <c>type</c> values, the mobile-number format and the success-response pattern
/// must all be supplied from the provider's confirmed contract. Until they are, the Broadnet sender
/// reports itself not configured and nothing is sent. Secrets belong in environment variables or a
/// secret store (<c>Sms__Broadnet__Password</c>), never in a committed file.
/// </summary>
public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    /// <summary><c>Disabled</c> (default) | <c>Broadnet</c> | <c>Fake</c> (Development/Testing only — <see cref="SmsSafety"/> refuses it elsewhere).</summary>
    public string Provider { get; set; } = "Disabled";

    public BroadnetSmsOptions Broadnet { get; set; } = new();

    public FakeSmsOptions Fake { get; set; } = new();
}

public enum MobileNumberFormat
{
    /// <summary>Not chosen yet — the sender stays unconfigured.</summary>
    Unset = 0,

    /// <summary>Country code + number, digits only, e.g. <c>971501234567</c>.</summary>
    InternationalDigits = 1,

    /// <summary>Leading plus, e.g. <c>+971501234567</c>.</summary>
    PlusInternational = 2
}

public sealed class BroadnetSmsOptions
{
    /// <summary>Absolute <b>https</b> URL of the provider's send action, without a query string. Plain http is refused.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Sent as <c>user</c>.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Sent as <c>pass</c>. Secret.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Approved sender id; sent as <c>sid</c>.</summary>
    public string SenderId { get; set; } = string.Empty;

    /// <summary>The provider's <c>type</c> value for English messages. Not guessed.</summary>
    public string TypeEnglish { get; set; } = string.Empty;

    /// <summary>The provider's <c>type</c> value for Arabic (Unicode) messages. Not guessed.</summary>
    public string TypeArabic { get; set; } = string.Empty;

    public MobileNumberFormat MobileFormat { get; set; } = MobileNumberFormat.Unset;

    /// <summary>
    /// Regex a <b>2xx</b> response body must match to count as acceptance. Required — a response that is
    /// neither a recognised success nor a recognised failure is never reported as sent. An optional named
    /// group <c>ref</c> captures the provider's message reference.
    /// </summary>
    public string SuccessBodyPattern { get; set; } = string.Empty;

    /// <summary>Optional regex that marks a 2xx body as an explicit provider failure (checked first).</summary>
    public string FailureBodyPattern { get; set; } = string.Empty;

    /// <summary>Overall call budget. On expiry the outcome is <c>Unconfirmed</c>, never a retry.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class FakeSmsOptions
{
    /// <summary>Log the full message text (including the code) — Development convenience only.</summary>
    public bool LogMessageText { get; set; }
}
