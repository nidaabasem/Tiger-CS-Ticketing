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
/// <b>IsUnitRelated — governs the CRM-verification path, not eligibility
/// for becoming a ticket.</b> Every intake, unit-related or not, can be
/// promoted to a ticket once a category has been selected; CRM
/// verification (a <c>VerificationSession</c>, unit/contact references) is
/// required only when the request is unit-related. A non-unit-related
/// intake is promoted directly, with no unit/contact reference and no
/// verification step — see <c>TicketCreationAppService.CreateFromNonUnitIntakeAsync</c>
/// and <see cref="TigerCS.Domain.Modules.Ticketing.Ticket.CreateNonUnitRelated"/>.
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
    /// <b>No longer restricted to unit-related intakes.</b> Every intake,
    /// whether unit-related or not, can be promoted to a ticket once a
    /// category has been selected — CRM verification is required only for
    /// the unit-related path (enforced upstream, by the caller choosing
    /// which creation flow to invoke, not by this method). This method
    /// itself never inspects <see cref="IsUnitRelated"/>.
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
