using TigerCS.Domain.Infrastructure;

namespace TigerCS.Application.Modules.Notifications.Abstractions;

/// <summary>
/// One consumer of one Outbox <c>EventType</c>. The dispatcher owns claiming,
/// retry accounting and dead-lettering; a handler owns only "what this event
/// means".
///
/// <para>
/// <b>Every handler must be idempotent</b> (ADR-0013's own consequence: "every
/// consumer of an Outbox message must itself be idempotent"). Delivery is
/// at-least-once — a process that dies between a successful send and its
/// commit will re-run this handler — so a handler that cannot recognise
/// already-done work will duplicate a customer-visible effect.
/// </para>
/// </summary>
public interface IOutboxEventHandler
{
    /// <summary>The <c>OutboxMessages.EventType</c> value this handler consumes.</summary>
    string EventType { get; }

    Task<OutboxHandlingResult> HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}

/// <summary>How one handler invocation ended, in the dispatcher's terms.</summary>
public enum OutboxHandlingOutcome
{
    /// <summary>The effect happened, or was already recorded as having happened. Terminal.</summary>
    Succeeded = 1,

    /// <summary>Failed in a way a later attempt might survive. Stays pending until the attempt budget runs out.</summary>
    TransientFailure = 2,

    /// <summary>
    /// Failed in a way no retry can fix — a recipient that cannot be
    /// resolved, a payload that will never parse. Dead-letters at once
    /// rather than spending four more attempts proving the same thing. Still
    /// retained and visible, never discarded (FR-NOT-05).
    /// </summary>
    PermanentFailure = 3
}

/// <param name="Outcome">How the invocation ended.</param>
/// <param name="Error">Operator-facing diagnostic, persisted to <c>OutboxMessages.LastError</c>. Never a message body, address or secret.</param>
public sealed record OutboxHandlingResult(OutboxHandlingOutcome Outcome, string? Error = null)
{
    public static OutboxHandlingResult Succeeded() => new(OutboxHandlingOutcome.Succeeded);

    public static OutboxHandlingResult Transient(string error) => new(OutboxHandlingOutcome.TransientFailure, error);

    public static OutboxHandlingResult Permanent(string error) => new(OutboxHandlingOutcome.PermanentFailure, error);
}
