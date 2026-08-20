namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// MVP-ERD.md §2.8 / MVP-Data-Dictionary.md §2.8 (ADR-0007) — the immutable,
/// write-once record of exactly what the agent read back and the requester
/// confirmed, copied from a consumed VerificationSession's own snapshot
/// fields (Finding DR-01) — never a fresh CRM/cache read, and never updated
/// after insert (no method on this class mutates any field once
/// constructed; a future contributor proposing one is a defect, per this
/// entity's own ERD note).
///
/// <para>
/// <b>Relationship to Ticket — 0/1:1, not the mandatory 1:1 the ERD
/// describes, for a provisional ticket only.</b> See Ticket's own remarks
/// for why: a provisional (ISSUE-006) ticket has no requester to snapshot
/// yet. A <see cref="CrmVerificationStatus.Verified"/> ticket — whether
/// verified from the moment it was created, or reconciled later — always
/// has exactly one of these rows, matching the ERD's rule for that case
/// unchanged.
/// </para>
/// </summary>
public class TicketRequesterSnapshot
{
    public long TicketId { get; private set; }
    public string SnapshotUnitNumber { get; private set; } = string.Empty;
    public string? SnapshotPropertyName { get; private set; }
    public string? SnapshotTowerName { get; private set; }
    public string? SnapshotUnitType { get; private set; }
    public string? SnapshotContactDisplayName { get; private set; }
    public string? SnapshotContactChannel { get; private set; }
    public DateTime CapturedAtUtc { get; private set; }

    private TicketRequesterSnapshot() { }

    public TicketRequesterSnapshot(
        long ticketId,
        string snapshotUnitNumber,
        string? snapshotPropertyName,
        string? snapshotTowerName,
        string? snapshotUnitType,
        string? snapshotContactDisplayName,
        string? snapshotContactChannel,
        DateTime capturedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(snapshotUnitNumber))
        {
            throw new ArgumentException("SnapshotUnitNumber is required.", nameof(snapshotUnitNumber));
        }

        TicketId = ticketId;
        SnapshotUnitNumber = snapshotUnitNumber;
        SnapshotPropertyName = snapshotPropertyName;
        SnapshotTowerName = snapshotTowerName;
        SnapshotUnitType = snapshotUnitType;
        SnapshotContactDisplayName = snapshotContactDisplayName;
        SnapshotContactChannel = snapshotContactChannel;
        CapturedAtUtc = capturedAtUtc;
    }
}
