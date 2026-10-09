namespace TigerCS.Application.Modules.Notifications.Abstractions;

public enum SmsSendOutcome
{
    /// <summary>The provider's response was verified as acceptance of the message. Not proof of handset delivery.</summary>
    Accepted,

    /// <summary>The provider refused the message (bad credentials/sender/number, or an explicit failure response).</summary>
    Rejected,

    /// <summary>The message was not accepted: provider unreachable, refused the connection, or answered with a server/throttle error.</summary>
    Failed,

    /// <summary>Timeout or an unreadable/unrecognised response — the SMS may or may not have been sent.</summary>
    Unconfirmed
}

/// <param name="Destination">Recipient mobile as CRM holds it (any common form); the sender formats it for its provider.</param>
/// <param name="Text">The full message, including the code — must never be logged.</param>
/// <param name="Language">"en" or "ar"; the sender maps it to the provider's own value.</param>
public sealed record SmsMessage(string Destination, string Text, string Language);

public sealed record SmsSendResult(SmsSendOutcome Outcome, string? ProviderReference = null);

/// <summary>
/// Port for the SMS provider. Implementations must never throw for an expected provider
/// answer, never log the message text or credentials, and never report
/// <see cref="SmsSendOutcome.Accepted"/> unless the provider's response was positively
/// recognised as success.
/// </summary>
public interface ISmsSender
{
    /// <summary>False until every setting the provider needs is present; the OTP flow answers "not configured" without creating a challenge.</summary>
    bool IsConfigured { get; }

    Task<SmsSendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken = default);
}
