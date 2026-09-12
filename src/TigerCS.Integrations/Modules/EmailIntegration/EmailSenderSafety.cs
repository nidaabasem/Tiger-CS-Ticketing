using System.Net.Mail;

namespace TigerCS.Integrations.Modules.EmailIntegration;

/// <summary>
/// Startup guards for the email pipeline, evaluated by <c>Program.cs</c>
/// before the host serves a request, so a misconfigured environment fails
/// loudly at deploy time rather than quietly recording "Sent" rows that no
/// customer ever received — or crashing on the first delivery.
/// </summary>
public static class EmailSenderSafety
{
    public static readonly IReadOnlyCollection<string> RecordingAllowedEnvironments = ["Development", "Testing"];

    /// <summary>The recording adapter pretends to deliver; outside Development/Testing that is a lie the dashboards would believe.</summary>
    public static bool IsUnsafe(string? provider, string environmentName) =>
        string.Equals(provider, EmailNotificationOptions.RecordingProvider, StringComparison.OrdinalIgnoreCase)
        && !RecordingAllowedEnvironments.Contains(environmentName);

    /// <summary>With delivery disabled the adapter is never asked to send, so the recording adapter is harmless anywhere.</summary>
    public static bool IsUnsafe(bool emailEnabled, string? provider, string environmentName) =>
        emailEnabled && IsUnsafe(provider, environmentName);

    /// <summary>
    /// Configuration errors that would make SMTP delivery fail on first use.
    /// Only evaluated when delivery is enabled with the SMTP provider — a
    /// disabled or recording configuration needs no credentials. Messages
    /// name the missing <i>key</i>, never a value.
    /// </summary>
    public static IReadOnlyList<string> Validate(EmailNotificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        if (!options.Enabled)
        {
            return errors;
        }

        if (!options.IsSmtp && !options.IsRecording)
        {
            errors.Add($"EmailNotifications:Provider '{options.Provider}' is not supported. Use \"Smtp\" or \"Recording\".");
            return errors;
        }

        if (!options.IsSmtp)
        {
            return errors;
        }

        if (string.IsNullOrWhiteSpace(options.SmtpHost))
        {
            errors.Add("EmailNotifications:SmtpHost is required when EmailNotifications:Enabled is true.");
        }

        if (options.SmtpPort is < 1 or > 65535)
        {
            errors.Add("EmailNotifications:SmtpPort must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            errors.Add("EmailNotifications:Username is required when EmailNotifications:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(options.Password))
        {
            errors.Add(
                "EmailNotifications:Password is required when EmailNotifications:Enabled is true. Supply it through "
                + "user-secrets or the EmailNotifications__Password environment variable — never in appsettings.json.");
        }

        if (string.IsNullOrWhiteSpace(options.FromEmail))
        {
            errors.Add("EmailNotifications:FromEmail is required when EmailNotifications:Enabled is true.");
        }
        else if (!MailAddress.TryCreate(options.FromEmail, out _))
        {
            errors.Add("EmailNotifications:FromEmail is not a valid email address.");
        }

        return errors;
    }
}
