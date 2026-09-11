using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// Resolves the customer a ticket's notifications go to — name and email —
/// from the ticket's own persisted data and nothing else.
///
/// <para>
/// <b>Sources, in order of trust.</b> No column on <c>Tickets</c> holds a
/// customer email, and this increment deliberately adds none: two reliable
/// fields already exist and are consulted in this order —
/// </para>
/// <list type="number">
///   <item>
///   <see cref="TicketRequesterSnapshot.SnapshotContactChannel"/> — the CRM
///   contact verified at ticket creation (ADR-0007's immutable snapshot).
///   It is an untyped "phone or email" value, so it counts only when it is
///   unambiguously an email address.
///   </item>
///   <item>
///   <see cref="TicketInteraction.CustomerEmail"/> — the email Genesys
///   supplied with the inquiry, captured on the originating interaction
///   (falling back to the most recent interaction that carries one).
///   </item>
/// </list>
/// <para>
/// <b>Never guesses.</b> A ticket with neither yields
/// <see cref="CustomerContact.SkipReason"/> = <see cref="CustomerEmailSkipReasons.NoCustomerEmail"/>;
/// a value that exists but is not a usable address yields
/// <see cref="CustomerEmailSkipReasons.InvalidCustomerEmail"/>. There is no
/// fallback mailbox, and the reason never echoes the value itself.
/// </para>
/// </summary>
public sealed class CustomerContactResolver(
    ITicketRequesterSnapshotRepository snapshotRepository,
    ITicketInteractionRepository interactionRepository)
{
    public async Task<CustomerContact> ResolveAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var snapshot = await snapshotRepository.GetByTicketIdAsync(ticket.TicketId, cancellationToken);
        var interactions = await interactionRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken);
        var originating = interactions.FirstOrDefault(i => i.IsOriginatingInteraction);

        var displayName = FirstNonBlank(
            snapshot?.SnapshotContactDisplayName,
            ticket.CrmBuyerCustomerName,
            originating?.CustomerName,
            interactions.Select(i => i.CustomerName).LastOrDefault(n => !string.IsNullOrWhiteSpace(n)));

        var candidates = new List<string?>
        {
            snapshot?.SnapshotContactChannel,
            originating?.CustomerEmail
        };
        candidates.AddRange(interactions
            .OrderByDescending(i => i.CreatedAtUtc)
            .Where(i => !i.IsOriginatingInteraction)
            .Select(i => i.CustomerEmail));

        var sawCandidate = false;
        foreach (var candidate in candidates)
        {
            var normalized = CustomerEmailAddress.Normalize(candidate);
            if (normalized is null)
            {
                continue;
            }

            sawCandidate = true;
            if (CustomerEmailAddress.IsValid(normalized))
            {
                return CustomerContact.Deliverable(displayName, normalized);
            }
        }

        return CustomerContact.NotDeliverable(
            displayName,
            sawCandidate ? CustomerEmailSkipReasons.InvalidCustomerEmail : CustomerEmailSkipReasons.NoCustomerEmail);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}

/// <param name="DisplayName">Customer name for the greeting, when any source recorded one.</param>
/// <param name="EmailAddress">A validated, trimmed address — or <c>null</c> when none is usable.</param>
/// <param name="SkipReason">A <see cref="CustomerEmailSkipReasons"/> code when <see cref="EmailAddress"/> is <c>null</c>.</param>
public sealed record CustomerContact(string? DisplayName, string? EmailAddress, string? SkipReason)
{
    public bool IsDeliverable => EmailAddress is not null;

    public static CustomerContact Deliverable(string? displayName, string emailAddress) => new(displayName, emailAddress, null);

    public static CustomerContact NotDeliverable(string? displayName, string skipReason) => new(displayName, null, skipReason);
}
