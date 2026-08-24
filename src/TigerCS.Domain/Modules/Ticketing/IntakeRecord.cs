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
/// <b>IsUnitRelated — determines whether CRM verification gates promotion,
/// not whether promotion is possible at all.</b> Every intake, unit-related
/// or not, may be promoted to a <see cref="Ticket"/> once a supported Ticket
/// Category is selected (<see cref="LinkToTicket"/>). A unit-related
/// interaction is additionally required to pass CRM verification first —
/// enforced by <c>TicketCreationAppService</c>'s unit-related creation paths,
/// which require a verified (or ISSUE-006-approved provisional) CRM outcome
/// before a ticket is ever created. A non-unit-related interaction has
/// nothing to verify against the CRM and promotes directly. Many intake
/// attempts still never become a ticket at all (e.g. a wrong-number call, a
/// simple information request answered verbally) — that remains a choice the
/// calling agent makes, not a restriction this column encodes.
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

    /// <summary>
    /// Links this record to the ticket it was promoted into and records the
    /// reconciliation outcome — write-once (a second promotion attempt is a
    /// defect, not a valid state, and is rejected by the caller before this
    /// is ever invoked twice).
    ///
    /// <para>
    /// <b>Business-rule change: no longer restricted to unit-related
    /// records.</b> Every intake — unit-related or not — may be promoted to a
    /// ticket once a supported Ticket Category is selected; CRM verification
    /// is required only when <see cref="IsUnitRelated"/> is true (enforced by
    /// the caller before this is ever invoked — the ticket-creation app
    /// service routes unit-related intakes through the verification/
    /// provisional paths, which already require a verified or approved-
    /// provisional CRM outcome before calling this method).
    /// </para>
    /// </summary>
    public void LinkToTicket(long ticketId, CrmVerificationStatus resultingStatus)
    {
        if (LinkedTicketId is not null)
        {
            throw new IntakeRecordAlreadyLinkedException(IntakeRecordId, LinkedTicketId.Value);
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
