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
/// <param name="Body">Rendered plain-text body.</param>
/// <param name="CorrelationId">ADR-0014's correlation ID, propagated into provider calls so a delivery is traceable end-to-end.</param>
public sealed record EmailMessage(string ToAddress, string Subject, string Body, Guid CorrelationId);

/// <summary>Whether a send succeeded, may succeed later, or never will.</summary>
public enum EmailSendOutcome
{
    /// <summary>The provider accepted the message for delivery.</summary>
    Sent = 1,

    /// <summary>A failure a later attempt could plausibly survive — timeout, throttling, provider outage.</summary>
    TransientFailure = 2,

    /// <summary>A failure no retry can fix — a rejected address, a refused sender identity. Dead-letters immediately rather than burning the whole retry budget on a certainty.</summary>
    PermanentFailure = 3
}

/// <param name="Outcome">How the attempt ended.</param>
/// <param name="Error">Operator-facing diagnostic. Must not contain the message body or credentials — it is persisted to <c>OutboxMessages.LastError</c> and surfaced to administrators.</param>
public sealed record EmailSendResult(EmailSendOutcome Outcome, string? Error = null)
{
    public static EmailSendResult Sent() => new(EmailSendOutcome.Sent);

    public static EmailSendResult Transient(string error) => new(EmailSendOutcome.TransientFailure, error);

    public static EmailSendResult Permanent(string error) => new(EmailSendOutcome.PermanentFailure, error);
}
