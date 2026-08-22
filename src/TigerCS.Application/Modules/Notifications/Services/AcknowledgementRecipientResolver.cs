using System.Net.Mail;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// Decides where — if anywhere — a ticket's automated acknowledgement may be
/// sent.
///
/// <para>
/// <b>The verified snapshot is the only source, and this is a hard rule.</b>
/// ADR-0006 makes the CRM the sole source of truth for contacts and ADR-0007
/// freezes what the requester actually confirmed into
/// <c>TicketRequesterSnapshots</c> at verification time. This resolver reads
/// that row and nothing else: it never calls the CRM, never reads the
/// <c>ContactReferences</c> cache (which is refreshable and may have changed
/// since verification), and never writes any CRM-owned data. A ticket whose
/// requester was verified in March is acknowledged at the address confirmed
/// in March, which is the whole point of an immutable snapshot.
/// </para>
///
/// <para>
/// <b>Nothing is ever guessed.</b> There is no fallback to the creating
/// agent, to a department mailbox, or to any staff address. Sending a
/// customer's ticket details to a substituted recipient would be a
/// disclosure incident, and no merged document authorises one.
/// </para>
///
/// <para>
/// <b>Reported gap — no approved email column exists (CRITICAL).</b>
/// FR-NOT-01 requires an email acknowledgement and
/// MVP-Data-Dictionary.md §2.21 provides <c>RecipientAddress nvarchar(320)</c>
/// to hold the address, but §2.8's snapshot carries only
/// <c>SnapshotContactChannel</c>, which <c>Internal-CRM-API-Contract.md</c>
/// §5 defines as "the phone/email on file for this contact — used to
/// cross-check that the caller matches CRM's own record": one free-text
/// field, no type discriminator, cached for identity verification rather than
/// for delivery. MVP intake is phone-only, so the common value is a phone
/// number. No merged document says which field to send to, or what to do when
/// the value is not an address.
/// </para>
///
/// <para>
/// Rather than invent a rule, this resolver accepts the channel value <i>only
/// when it is already a syntactically valid email address</i> and reports
/// <see cref="RecipientResolution.NoDeliverableAddress"/> otherwise. That
/// outcome is a recorded, dead-lettered, audited, operator-visible fact — not
/// a silent skip and not a guess. <b>A business decision is required</b> on
/// where a requester's email address comes from.
/// </para>
/// </summary>
public static class AcknowledgementRecipientResolver
{
    public static RecipientResolution Resolve(TicketRequesterSnapshot? snapshot)
    {
        // A provisional (CRM-outage, ISSUE-006) ticket has no snapshot at
        // all — there was no reachable CRM to verify a requester against. It
        // is not an error, and it is emphatically not licence to pick a
        // recipient: there is simply nobody verified to write to yet.
        if (snapshot is null)
        {
            return RecipientResolution.NoDeliverableAddress(
                "No verified requester snapshot exists for this ticket (provisional/unverified ticket).");
        }

        var channel = snapshot.SnapshotContactChannel?.Trim();
        if (string.IsNullOrEmpty(channel))
        {
            return RecipientResolution.NoDeliverableAddress(
                "The verified requester snapshot records no contact channel.");
        }

        if (!IsEmailAddress(channel))
        {
            // The diagnostic deliberately says only that the value is not an
            // address — it never echoes the value itself, which is customer
            // contact data and would end up in OutboxMessages.LastError and
            // in an operator-visible queue (Security-Architecture.md §11).
            return RecipientResolution.NoDeliverableAddress(
                "The verified requester snapshot's contact channel is not an email address "
                + "(MVP-Data-Dictionary.md §2.8 stores one untyped phone-or-email value; "
                + "no approved email column exists).");
        }

        return RecipientResolution.Deliverable(channel);
    }

    /// <summary>
    /// Syntactic only, and intentionally strict: a value must parse as a
    /// single mailbox <i>and</i> round-trip to itself, which rejects the
    /// display-name forms <see cref="MailAddress"/> otherwise accepts (a bare
    /// phone number parses as a display name with no address on some inputs)
    /// and rejects a domain with no dot. Deliverability is the provider's
    /// judgement, not this method's — a well-formed address that bounces is a
    /// permanent send failure, handled downstream.
    /// </summary>
    private static bool IsEmailAddress(string value)
    {
        if (value.Contains(' ', StringComparison.Ordinal) || value.Count(c => c == '@') != 1)
        {
            return false;
        }

        if (!MailAddress.TryCreate(value, out var parsed) || parsed is null)
        {
            return false;
        }

        return string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase)
            && parsed.Host.Contains('.', StringComparison.Ordinal)
            && !parsed.Host.StartsWith('.')
            && !parsed.Host.EndsWith('.');
    }
}

/// <param name="Address">The resolved address, or <c>null</c> when none could be resolved.</param>
/// <param name="Reason">Why no address could be resolved. Never contains the rejected value itself.</param>
public sealed record RecipientResolution(string? Address, string? Reason)
{
    public bool IsDeliverable => Address is not null;

    public static RecipientResolution Deliverable(string address) => new(address, null);

    public static RecipientResolution NoDeliverableAddress(string reason) => new(null, reason);
}
