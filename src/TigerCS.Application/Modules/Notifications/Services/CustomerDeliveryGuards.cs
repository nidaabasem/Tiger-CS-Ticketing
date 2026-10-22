using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.Notifications.Abstractions;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// The final delivery boundary for email: wraps the provider adapter so that <c>CustomerNotifications:Mode</c> is enforced for
/// <i>every</i> caller — ticket notifications, OTP codes, document copies, Collections reminders, anything added later —
/// including background jobs. No code path can reach the provider with a customer-facing message except through this class.
///
/// <list type="bullet">
///   <item><see cref="EmailAudience.Internal"/> (staff, administrators): always passes.</item>
///   <item><see cref="EmailAudience.Customer"/> (the default): passes only in Production mode.</item>
///   <item><see cref="EmailAudience.TestMailbox"/>: passes only when addressed to a configured test inbox
///   (<c>CustomerNotifications:TestEmail</c> or the <c>VerificationTesting</c> override) and the mode is valid.</item>
/// </list>
///
/// A refused message is reported as <see cref="EmailSendOutcome.Suppressed"/> — terminal, never retried, never an error.
/// </summary>
public sealed class CustomerDeliveryGuardEmailSender(
    IEmailSender inner,
    CustomerNotificationModePolicy mode,
    VerificationTestingPolicy? verificationTesting,
    ILogger<CustomerDeliveryGuardEmailSender> logger) : IEmailSender
{
    /// <summary>The provider behind the guard.</summary>
    public IEmailSender Inner => inner;

    public bool SendsRealMail => inner.SendsRealMail;

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var refusal = Refusal(message);
        if (refusal is null)
        {
            return inner.SendAsync(message, cancellationToken);
        }

        logger.LogWarning(
            "Customer-facing email suppressed at the delivery boundary ({Reason}); audience {Audience}, mode {Mode}. CorrelationId {CorrelationId}.",
            refusal, message.Audience, mode.Mode, message.CorrelationId);
        return Task.FromResult(EmailSendResult.Suppressed(refusal));
    }

    private string? Refusal(EmailMessage message)
    {
        switch (message.Audience)
        {
            case EmailAudience.Internal:
                return null;

            case EmailAudience.Customer:
                return mode.IsProduction
                    ? null
                    : mode.IsTest
                        ? CustomerEmailSkipReasons.TestModeSuppressed
                        : CustomerEmailSkipReasons.CustomerNotificationModeInvalid;

            default:
                if (mode.Mode == CustomerNotificationMode.Invalid)
                {
                    return CustomerEmailSkipReasons.CustomerNotificationModeInvalid;
                }

                return mode.IsTestMailbox(message.ToAddress) || (verificationTesting?.IsApprovedEmailOverride(message.ToAddress) ?? false)
                    ? null
                    : CustomerEmailSkipReasons.TestEmailMissing;
        }
    }
}

/// <summary>
/// SMS counterpart of <see cref="CustomerDeliveryGuardEmailSender"/>. Every SMS this system sends is customer-facing (the
/// verification code), so Production passes, Test passes only a message redirected to the configured test number, and an
/// invalid mode passes nothing.
/// </summary>
public sealed class CustomerDeliveryGuardSmsSender(
    ISmsSender inner,
    CustomerNotificationModePolicy mode,
    VerificationTestingPolicy? verificationTesting,
    ILogger<CustomerDeliveryGuardSmsSender> logger) : ISmsSender
{
    /// <summary>The provider behind the guard.</summary>
    public ISmsSender Inner => inner;

    public bool IsConfigured => inner.IsConfigured;

    public Task<SmsSendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var allowed = mode.Mode switch
        {
            CustomerNotificationMode.Production => true,
            CustomerNotificationMode.Test => message.TestRedirected && (verificationTesting?.IsApprovedSmsOverride(message.Destination) ?? false),
            _ => false
        };

        if (allowed)
        {
            return inner.SendAsync(message, cancellationToken);
        }

        logger.LogWarning("Customer SMS suppressed at the delivery boundary; mode {Mode}.", mode.Mode);
        return Task.FromResult(new SmsSendResult(SmsSendOutcome.Suppressed));
    }
}
