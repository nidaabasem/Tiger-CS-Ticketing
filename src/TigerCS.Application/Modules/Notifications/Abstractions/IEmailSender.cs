namespace TigerCS.Application.Modules.Notifications.Abstractions;

/// <summary>
/// The email-provider boundary — Module-Design.md's <c>IEmailGateway</c>
/// "external adapter contract" for the Notifications module, named
/// <c>IEmailSender</c> to say what it does rather than what layer it lives
/// in.
///
/// <para>
/// <b>Deliberately the narrowest possible surface.</b> One operation, a
/// value-typed message in, a classified result out. No provider concept
/// (SMTP host, Graph client, API key, tenant) crosses it, so provider
/// configuration cannot leak into business logic and swapping Office 365 for
/// an SMTP relay touches one adapter and nothing else. The same shape
/// <c>ICrmGateway</c> already uses.
/// </para>
///
/// <para>
/// <b>Returns a classified result rather than throwing.</b> Whether a
/// failure is worth retrying is provider knowledge — a 4xx "no such mailbox"
/// and a 5xx "greylisted, try later" are not the same event, and the
/// dispatcher must not have to guess by inspecting exception types it does
/// not own. An adapter that throws anyway is still handled (the dispatcher
/// treats an unclassified exception as transient, the safer direction), but
/// classifying is the contract.
/// </para>
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// False for an adapter that records instead of sending (<c>EmailNotifications:Provider = "Recording"</c>): its "Sent" means
    /// "kept in memory", not "accepted by a mail server". Callers that tell a person a message was accepted check this first.
    /// </summary>
    bool SendsRealMail => true;

    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// One outbound email. Carries the rendered body but is never persisted and
/// never logged — MVP-Data-Dictionary.md §2.21 has no body column, and
/// Security-Architecture.md §11 requires contact details be masked or omitted
/// from log output.
/// </summary>
/// <param name="ToAddress">The verified requester's address. Resolution is the caller's job; an adapter never guesses one.</param>
/// <param name="Subject">Rendered subject line.</param>
/// <param name="Body">Rendered plain-text body — always present, so a client that cannot render HTML still gets the message.</param>
/// <param name="CorrelationId">ADR-0014's correlation ID, propagated into provider calls so a delivery is traceable end-to-end.</param>
/// <param name="HtmlBody">Optional rendered HTML alternative. Adapters that support multipart mail send both; the plain text is the fallback.</param>
/// <param name="Attachments">Optional files (customer document copies). Held in memory for the send only.</param>
/// <param name="Audience">
/// Who the recipient is. Defaults to <see cref="EmailAudience.Customer"/> so that a caller which forgets to say is treated as
/// customer-facing and is therefore subject to <c>CustomerNotifications:Mode</c> — the safe direction.
/// </param>
public sealed record EmailMessage(
    string ToAddress, string Subject, string Body, Guid CorrelationId, string? HtmlBody = null,
    IReadOnlyList<EmailAttachment>? Attachments = null, EmailAudience Audience = EmailAudience.Customer);

/// <summary>Who an <see cref="EmailMessage"/> is for. The final delivery guard applies <c>CustomerNotifications:Mode</c> to everything that is not <see cref="Internal"/>.</summary>
public enum EmailAudience
{
    /// <summary>A customer's mailbox. Delivered only in <c>CustomerNotifications:Mode = Production</c>.</summary>
    Customer = 0,

    /// <summary>A staff or administrator mailbox. Never affected by the customer notification mode.</summary>
    Internal = 1,

    /// <summary>A configured test inbox standing in for a customer (<c>CustomerNotifications:TestEmail</c> or <c>VerificationTesting:EmailOverride</c>). Delivered only to such an inbox.</summary>
    TestMailbox = 2
}

/// <summary>A file sent with an email — used for customer document copies. Held in memory only for the length of the send; never logged and never persisted by the sender.</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>Whether a send succeeded, may succeed later, or never will.</summary>
public enum EmailSendOutcome
{
    /// <summary>The provider accepted the message for delivery.</summary>
    Sent = 1,

    /// <summary>A failure a later attempt could plausibly survive — timeout, throttling, provider outage.</summary>
    TransientFailure = 2,

    /// <summary>A failure no retry can fix — a rejected address, a refused sender identity. Dead-letters immediately rather than burning the whole retry budget on a certainty.</summary>
    PermanentFailure = 3,

    /// <summary>Deliberately not sent: the customer notification mode forbids it. Terminal, expected, and never retried.</summary>
    Suppressed = 4
}

/// <param name="Outcome">How the attempt ended.</param>
/// <param name="Error">Operator-facing diagnostic. Must not contain the message body or credentials — it is persisted to <c>OutboxMessages.LastError</c> and surfaced to administrators.</param>
public sealed record EmailSendResult(EmailSendOutcome Outcome, string? Error = null)
{
    public static EmailSendResult Sent() => new(EmailSendOutcome.Sent);

    public static EmailSendResult Transient(string error) => new(EmailSendOutcome.TransientFailure, error);

    public static EmailSendResult Permanent(string error) => new(EmailSendOutcome.PermanentFailure, error);

    public static EmailSendResult Suppressed(string reason) => new(EmailSendOutcome.Suppressed, reason);
}
