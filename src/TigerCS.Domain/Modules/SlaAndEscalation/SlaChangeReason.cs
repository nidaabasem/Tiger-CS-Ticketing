namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md §2.15 — <c>TicketSlaInstances.ChangeReason</c>
/// ("e.g., InitialCreation/Upgrade/Downgrade").
///
/// <para>
/// Only <see cref="InitialCreation"/> is ever written by this increment.
/// <see cref="Upgrade"/> belongs to backlog S-14 (priority upgrade) and
/// <see cref="Downgrade"/> to the post-pilot downgrade-approval workflow —
/// which MVP-Implementation-Backlog.md §0 hard-disables for the pilot
/// ("Priority is fixed after ticket creation during the pilot. Downgrades
/// are not permitted."). Both members exist because the column's approved
/// value set includes them (MVP-Data-Dictionary.md §2.15), not because this
/// increment produces them; a row carrying either value cannot be created by
/// any code path that ships here.
/// </para>
/// </summary>
public enum SlaChangeReason : byte
{
    InitialCreation = 1,
    Upgrade = 2,
    Downgrade = 3
}
