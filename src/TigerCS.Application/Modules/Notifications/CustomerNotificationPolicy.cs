namespace TigerCS.Application.Modules.Notifications;

/// <summary>
/// The Application layer's view of the <c>EmailNotifications</c>
/// configuration section — the same shape as <see cref="OutboxDispatchPolicy"/>:
/// a plain value the composition root builds from <c>IOptions</c>, so no
/// options or configuration type reaches business code.
/// </summary>
/// <param name="Enabled">
/// Master switch for customer email notifications. When <c>false</c> every
/// ticket operation behaves exactly as before — the Outbox event is still
/// recorded, but its handler marks the notification <c>Skipped</c> instead of
/// contacting any provider.
/// </param>
/// <param name="MaxEventAge">
/// An event older than this when its handler first runs is skipped rather
/// than emailed. Protects customers from a burst of stale "your request has
/// been received" mail when the dispatcher (or the feature) is switched on
/// after a backlog of Outbox rows has accumulated. <see cref="TimeSpan.Zero"/>
/// disables the check.
/// </param>
/// <param name="IncludeResolutionNote">
/// Whether the agent-written <c>TicketResolution.ResolutionNote</c> is quoted
/// in the "resolved" email. Off by default: the note is a mandatory internal
/// field (BR-011) with no customer-safe flag, so it is only shared once the
/// business confirms agents write it for the customer.
/// </param>
public sealed record CustomerNotificationPolicy(bool Enabled, TimeSpan MaxEventAge, bool IncludeResolutionNote)
{
    public static CustomerNotificationPolicy Default { get; } = new(
        Enabled: false, MaxEventAge: TimeSpan.FromHours(24), IncludeResolutionNote: false);

    public static CustomerNotificationPolicy Disabled { get; } = Default;

    public static CustomerNotificationPolicy EnabledDefault { get; } = Default with { Enabled = true };
}
