namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// MVP-ERD.md §2.9 / MVP-Data-Dictionary.md §2.9 — the first, unconditional
/// record of any customer interaction, created before any customer lookup
/// and before a Ticket exists, so no interaction is ever silently lost
/// regardless of whether it turns out to be unit-related, whether a customer
/// match is found, or whether it ever becomes a ticket at all (many intake
/// attempts never do — e.g. a wrong-number call, a simple information
/// request answered verbally).
///
/// <para>
/// <b>PhoneNumber — captured once, preserved regardless of lookup outcome.</b>
/// The identifier used to search for the customer across CRM, PACT, and
/// Tasleeh (<c>CustomerLookupAppService</c>). It never changes based on what
/// that search finds — Found, NotFound, or Failed all leave it exactly as the
/// agent entered it, so a later re-lookup or manual reconciliation always has
/// the original value to search with.
/// </para>
///
/// <para>
/// <b>IsUnitRelated — no longer a promotion gate.</b> Every intake,
/// unit-related or not, may be promoted to a <see cref="Ticket"/> once a
/// supported Ticket Category is selected (<see cref="LinkToTicket"/>).
/// Customer lookup (CRM/PACT/Tasleeh) is enrichment/identification, not a
/// promotion gate either — whether a match is Found, NotFound, or the source
/// Failed to answer, ticket creation proceeds the same way; a found match
/// only supplies the Ticket's optional <c>UnitReferenceId</c>/
/// <c>ContactReferenceId</c>. Many intake attempts still never become a
/// ticket at all (e.g. a wrong-number call, a simple information request
/// answered verbally) — that remains a choice the calling agent makes, not a
/// restriction this column encodes.
/// </para>
/// </summary>
public class IntakeRecord
{
    public long IntakeRecordId { get; private set; }
    public Channel ChannelId { get; private set; }
    public DateTime ReceivedAtUtc { get; private set; }
    public string PhoneNumber { get; private set; } = string.Empty;
    public bool IsUnitRelated { get; private set; }
    public string? RawUnitNumberEntered { get; private set; }
    public byte? PriorityHint { get; private set; }
    public CrmVerificationStatus CrmVerificationStatus { get; private set; }
    public Guid CreatedByEmployeeId { get; private set; }
    public long? LinkedTicketId { get; private set; }

    private IntakeRecord() { }

    public IntakeRecord(
        Channel channelId,
        string phoneNumber,
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

        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            throw new ArgumentException(
                "PhoneNumber is required — it is the identifier customer lookup searches CRM/PACT/Tasleeh with.",
                nameof(phoneNumber));
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
        PhoneNumber = phoneNumber;
        IsUnitRelated = isUnitRelated;
        RawUnitNumberEntered = rawUnitNumberEntered;
        PriorityHint = priorityHint;
        CreatedByEmployeeId = createdByEmployeeId;
        ReceivedAtUtc = receivedAtUtc;
        CrmVerificationStatus = CrmVerificationStatus.Unverified;
    }

    /// <summary>
    /// Links this record to the ticket it was promoted into and records the
    /// resulting customer-match status — write-once (a second promotion
    /// attempt is a defect, not a valid state, and is rejected by the caller
    /// before this is ever invoked twice).
    ///
    /// <para>
    /// Every intake — unit-related or not, customer match found or not —
    /// may be promoted to a ticket once a supported Ticket Category is
    /// selected. A lookup outcome of NotFound or Failed is recorded here
    /// exactly like Found; none of the three ever blocks this call.
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
}
