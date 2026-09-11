namespace TigerCS.Integrations.Modules.EmailIntegration;

/// <summary>
/// The <c>EmailNotifications</c> configuration section — everything the
/// customer email pipeline needs, in one place, bound with the Options
/// pattern like every other integration section.
///
/// <para>
/// <b>The password is never in a committed file.</b> <c>appsettings.json</c>
/// carries it as an empty string; UAT and Production supply
/// <c>EmailNotifications__Password</c> through the host's secret/environment
/// mechanism (see docs/Customer-Email-Notifications.md), Development through
/// <c>dotnet user-secrets</c>. <see cref="EmailSenderSafety.Validate"/>
/// refuses to start an environment that enables SMTP delivery without it.
/// </para>
/// </summary>
public sealed class EmailNotificationOptions
{
    public const string SectionName = "EmailNotifications";

    public const string SmtpProvider = "Smtp";

    public const string RecordingProvider = "Recording";

    /// <summary>
    /// Master switch. <c>false</c> (the default) means every ticket operation
    /// behaves exactly as before and each queued notification is recorded as
    /// <c>Skipped</c> — nothing contacts a mail server.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// <c>"Smtp"</c> — the Microsoft 365 SMTP account (the real adapter);
    /// <c>"Recording"</c> — the in-memory test adapter, refused outside
    /// Development/Testing when <see cref="Enabled"/> is true.
    /// </summary>
    public string Provider { get; set; } = SmtpProvider;

    public string SmtpHost { get; set; } = "smtp.office365.com";

    public int SmtpPort { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    /// <summary>The mailbox that authenticates to SMTP. Usually the same as <see cref="FromEmail"/>.</summary>
    public string? Username { get; set; }

    /// <summary>Supplied only via secret storage / environment. Never logged, never rendered into any error.</summary>
    public string? Password { get; set; }

    public string? FromEmail { get; set; }

    public string FromName { get; set; } = "Tiger Properties";

    /// <summary>SMTP send timeout. Bounded below by the adapter so a typo cannot disable the timeout.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// A queued notification older than this when first handled is skipped
    /// rather than sent (stale-backlog protection). <c>0</c> disables the check.
    /// </summary>
    public int MaxNotificationAgeHours { get; set; } = 24;

    /// <summary>Whether the agent's resolution note is quoted in the "resolved" email. Off until the business confirms it is customer-safe.</summary>
    public bool IncludeResolutionNote { get; set; }

    public bool IsSmtp => string.Equals(Provider, SmtpProvider, StringComparison.OrdinalIgnoreCase);

    public bool IsRecording => string.Equals(Provider, RecordingProvider, StringComparison.OrdinalIgnoreCase);

    public TigerCS.Application.Modules.Notifications.CustomerNotificationPolicy ToPolicy() => new(
        Enabled,
        MaxEventAge: TimeSpan.FromHours(Math.Clamp(MaxNotificationAgeHours, 0, 24 * 365)),
        IncludeResolutionNote);
}
