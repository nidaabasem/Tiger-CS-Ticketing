namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// MVP-ERD.md §2.9 / MVP-Data-Dictionary.md §2.9 — the first, unconditional
/// record of any customer interaction, created before verification and
/// before a Ticket exists, so no interaction is ever silently lost
/// regardless of whether it turns out to be unit-related, whether the CRM
/// is reachable, or whether it ever becomes a ticket at all (many intake
/// attempts never do — e.g. a wrong-number call, a simple information
/// request answered verbally).
///
/// <para>
/// <b>IsUnitRelated — a structural refinement, not a new business rule.</b>
/// Neither field is named in the approved Data Dictionary, but every
/// merged document (MVP-ERD.md, Tickets' NOT NULL Unit/Contact FKs,
/// FR-VER-01) models tickets as always unit-scoped, and ISSUE-006's
/// provisional-ticket rule is explicitly stated in terms of a unit-related
/// request. This column exists to let a general/non-unit interaction (which
/// never promotes to a ticket, matching this entity's own already-documented
/// "many intake attempts never become a ticket" assumption) be distinguished
/// from one this module will attempt to verify and promote.
/// </para>
/// </summary>
public class IntakeRecord
{
    public long IntakeRecordId { get; private set; }
    public Channel ChannelId { get; private set; }
    public DateTime ReceivedAtUtc { get; private set; }
    public bool IsUnitRelated { get; private set; }
    public string? RawUnitNumberEntered { get; private set; }
    public byte? PriorityHint { get; private set; }
    public CrmVerificationStatus CrmVerificationStatus { get; private set; }
    public Guid CreatedByEmployeeId { get; private set; }
    public long? LinkedTicketId { get; private set; }

    private IntakeRecord() { }

    public IntakeRecord(
        Channel channelId,
        bool isUnitRelated,
        string? rawUnitNumberEntered,
        byte? priorityHint,
        Guid createdByEmployeeId,
        DateTime receivedAtUtc)
    {
        if (createdByEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("CreatedByEmployeeId is required.", nameof(createdByEmployeeId));
        }

        if (isUnitRelated && string.IsNullOrWhiteSpace(rawUnitNumberEntered))
        {
            throw new ArgumentException(
                "RawUnitNumberEntered is required when IsUnitRelated is true — there is nothing to later verify against the CRM otherwise.",
                nameof(rawUnitNumberEntered));
        }

        if (!isUnitRelated && rawUnitNumberEntered is not null)
        {
            throw new ArgumentException(
                "RawUnitNumberEntered must be null for a non-unit-related interaction.", nameof(rawUnitNumberEntered));
        }

        ChannelId = channelId;
        IsUnitRelated = isUnitRelated;
        RawUnitNumberEntered = rawUnitNumberEntered;
        PriorityHint = priorityHint;
        CreatedByEmployeeId = createdByEmployeeId;
        ReceivedAtUtc = receivedAtUtc;
        CrmVerificationStatus = CrmVerificationStatus.Unverified;
    }

    /// <summary>Links this record to the ticket it was promoted into and records the reconciliation outcome — write-once (a second promotion attempt is a defect, not a valid state, and is rejected by the caller before this is ever invoked twice).</summary>
    public void LinkToTicket(long ticketId, CrmVerificationStatus resultingStatus)
    {
        if (LinkedTicketId is not null)
        {
            throw new IntakeRecordAlreadyLinkedException(IntakeRecordId, LinkedTicketId.Value);
        }

        if (!IsUnitRelated)
        {
            throw new IntakeRecordNotUnitRelatedException(IntakeRecordId);
        }

        LinkedTicketId = ticketId;
        CrmVerificationStatus = resultingStatus;
    }

    /// <summary>ISSUE-006's "Medium/Low remain queued" outcome — the interaction is captured and awaits CRM reconciliation, but does not (yet) become a ticket.</summary>
    public void MarkPendingCrmVerification()
    {
        if (!IsUnitRelated)
        {
            throw new IntakeRecordNotUnitRelatedException(IntakeRecordId);
        }

        if (LinkedTicketId is not null)
        {
            throw new IntakeRecordAlreadyLinkedException(IntakeRecordId, LinkedTicketId.Value);
        }

        CrmVerificationStatus = CrmVerificationStatus.PendingCrmVerification;
    }
}
