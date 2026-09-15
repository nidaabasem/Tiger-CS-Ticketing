namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md §2.15 — <c>TicketSlaInstances.ChangeReason</c>
/// ("e.g., InitialCreation/Upgrade/Downgrade").
///
/// <para>
/// <see cref="InitialCreation"/> and <see cref="Reopen"/> are the two reasons
/// this system writes. <see cref="Upgrade"/> belongs to backlog S-14
/// (priority upgrade) and <see cref="Downgrade"/> to the post-pilot
/// downgrade-approval workflow — which MVP-Implementation-Backlog.md §0
/// hard-disables for the pilot ("Priority is fixed after ticket creation
/// during the pilot. Downgrades are not permitted."). Both of those members
/// exist because the column's approved value set includes them
/// (MVP-Data-Dictionary.md §2.15), not because this increment produces them;
/// a row carrying either value cannot be created by any code path that ships
/// here.
/// </para>
/// </summary>
public enum SlaChangeReason : byte
{
    InitialCreation = 1,
    Upgrade = 2,
    Downgrade = 3,

    /// <summary>
    /// The approved Reopen rule's new Resolution SLA cycle: the closed period
    /// is ended at the reopen moment and this row opens the successor, so the
    /// original and the reopened cycle stay separately reportable. The column
    /// is already a tinyint with no value constraint, so this member needs no
    /// schema change — see <c>TicketSlaInstanceConfiguration</c>, whose only
    /// value-specific CHECK names Downgrade (3).
    /// </summary>
    Reopen = 4
}
