using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Notifications.Abstractions;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// The final delivery boundary for email: wraps the provider adapter so that <c>CustomerNotifications:IsTestMode</c> is applied
/// to <i>every</i> caller — ticket notifications, OTP codes, document copies, Collections reminders, anything added later —
/// including background jobs. No code path reaches the provider with a customer-facing message except through this class.
///
/// <list type="bullet">
///   <item><see cref="EmailAudience.Internal"/> (staff, administrators): always passes untouched.</item>
///   <item><see cref="EmailAudience.Customer"/> (the default, so a caller that forgets to classify is guarded): production
///   sends as addressed; test mode replaces the recipient with <c>TestEmail</c> (and marks the subject <c>[TEST]</c>), or —
///   when <c>TestEmail</c> is missing or invalid — skips the message and logs a warning. The customer's address is never used.</item>
/// </list>
///
/// A skipped message is reported as <see cref="EmailSendOutcome.Suppressed"/> — terminal, never retried, never an error.
/// </summary>
public sealed class CustomerDeliveryGuardEmailSender(
    IEmailSender inner,
    CustomerNotificationModePolicy mode,
    ILogger<CustomerDeliveryGuardEmailSender> logger) : IEmailSender
{
    /// <summary>The provider behind the guard.</summary>
    public IEmailSender Inner => inner;

    public bool SendsRealMail => inner.SendsRealMail;

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Audience == EmailAudience.Internal || mode.IsProduction)
        {
            return inner.SendAsync(message, cancellationToken);
        }

        var decision = mode.DecideEmail();
        if (!decision.Deliver)
        {
            logger.LogWarning(
                "Customer email skipped in test mode ({Reason}): {Warning} CorrelationId {CorrelationId}.",
                decision.SuppressReason, mode.ConfigurationWarning, message.CorrelationId);
            return Task.FromResult(EmailSendResult.Suppressed(decision.SuppressReason!));
        }

        var subject = message.Subject.StartsWith(CustomerNotificationModePolicy.TestSubjectPrefix, StringComparison.Ordinal)
            ? message.Subject
            : CustomerNotificationModePolicy.TestSubjectPrefix + message.Subject;
        return inner.SendAsync(message with { ToAddress = decision.Recipient!, Subject = subject }, cancellationToken);
    }
}

/// <summary>
/// SMS counterpart of <see cref="CustomerDeliveryGuardEmailSender"/>. Every SMS this system sends is customer-facing (the
/// verification code), so production sends as addressed and test mode replaces the destination with <c>TestPhone</c> — or skips
/// the SMS with a warning when <c>TestPhone</c> is missing or invalid.
/// </summary>
public sealed class CustomerDeliveryGuardSmsSender(
    ISmsSender inner,
    CustomerNotificationModePolicy mode,
    ILogger<CustomerDeliveryGuardSmsSender> logger) : ISmsSender
{
    /// <summary>The provider behind the guard.</summary>
    public ISmsSender Inner => inner;

    public bool IsConfigured => inner.IsConfigured;

    public Task<SmsSendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (mode.IsProduction)
        {
            return inner.SendAsync(message, cancellationToken);
        }

        var decision = mode.DecideSms();
        if (!decision.Deliver)
        {
            logger.LogWarning("Customer SMS skipped in test mode ({Reason}): {Warning}", decision.SuppressReason, mode.ConfigurationWarning);
            return Task.FromResult(new SmsSendResult(SmsSendOutcome.Suppressed));
        }

        return inner.SendAsync(message with { Destination = decision.Recipient! }, cancellationToken);
    }
}
