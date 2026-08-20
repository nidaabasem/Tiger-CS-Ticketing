using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// MVP-ERD.md §2.10 / MVP-Data-Dictionary.md §2.10 — the ticket aggregate
/// root and its five independent lifecycle dimensions (ADR-0008). This
/// increment implements only the ticket's creation moment (initial lifecycle
/// — MVP-Implementation-Backlog.md's later S-13/S-14/S-16 items: assignment,
/// transfer, status change, priority change, resolve/close — are out of
/// scope here and are not exposed by any method below).
///
/// <para>
/// <b>UnitReferenceId/ContactReferenceId/RequesterSnapshot nullability — a
/// confirmed, narrow relaxation, not a silent schema deviation.</b>
/// MVP-Data-Dictionary.md §2.10 marks both FKs NOT NULL and §2.8 says the
/// snapshot is "created in the same transaction as the ticket," which is
/// impossible for ISSUE-006's approved provisional-ticket rule (Critical/High
/// proceeds immediately during a CRM outage, before any CRM unit/contact has
/// been resolved — System-Architecture.md line 182). Confirmed during this
/// increment's pre-coding review: both become populated only once
/// <see cref="VerificationStatus"/> reaches <see cref="CrmVerificationStatus.Verified"/>
/// — either immediately (the normal verified-at-creation path,
/// <see cref="CreateVerified"/>) or later, at reconciliation
/// (<see cref="ReconcileVerification"/> — implemented and unit-tested now,
/// with no call site yet, matching the same forward-building pattern
/// VerificationSession.Consume() used ahead of this module's own arrival).
/// Nothing already-approved is removed: a <see cref="CrmVerificationStatus.Verified"/>
/// ticket still always has both.
/// </para>
/// </summary>
public class Ticket
{
    public long TicketId { get; private set; }
    public string TicketNumber { get; private set; } = string.Empty;
    public int OriginatingDepartmentId { get; private set; }
    public int CurrentDepartmentId { get; private set; }
    public Guid? CurrentOwnerEmployeeId { get; private set; }
    public int? UnitReferenceId { get; private set; }
    public int? ContactReferenceId { get; private set; }
    public int CategoryId { get; private set; }
    public byte PriorityId { get; private set; }

    public TicketStatus TicketStatus { get; private set; }
    public CrmVerificationStatus VerificationStatus { get; private set; }
    public EscalationLevel EscalationLevel { get; private set; }
    public SlaState SlaState { get; private set; }
    public byte? ResolutionOutcome { get; private set; }
    public long? DuplicateOfTicketId { get; private set; }

    public string RequestSummary { get; private set; } = string.Empty;
    public DateTime? FirstHumanResponseAtUtc { get; private set; }
    public DateTime? AcknowledgementSentAtUtc { get; private set; }
    public int ReopenCount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private Ticket() { }

    /// <summary>The normal path (FR-CH-01/FR-VER-02): a ticket created from an already-confirmed, just-consumed <c>VerificationSession</c>. Fully verified from the moment it exists.</summary>
    public static Ticket CreateVerified(
        string ticketNumber,
        int departmentId,
        int unitReferenceId,
        int contactReferenceId,
        int categoryId,
        byte priorityId,
        string requestSummary,
        DateTime createdAtUtc)
    {
        var ticket = CreateCore(ticketNumber, departmentId, categoryId, priorityId, requestSummary, createdAtUtc);
        ticket.UnitReferenceId = unitReferenceId;
        ticket.ContactReferenceId = contactReferenceId;
        ticket.VerificationStatus = CrmVerificationStatus.Verified;
        ticket.SlaState = SlaState.Running;
        return ticket;
    }

    /// <summary>ISSUE-006's approved fallback (Management-Decisions.md, System-Architecture.md line 182): Critical/High proceeds immediately, unverified, while the CRM is unreachable. No Unit/Contact reference and no requester snapshot exist yet — see this type's remarks for why that is a confirmed relaxation, not an oversight.</summary>
    public static Ticket CreateProvisional(
        string ticketNumber,
        int departmentId,
        int categoryId,
        byte priorityId,
        string requestSummary,
        DateTime createdAtUtc)
    {
        if (!Priority.IsCriticalOrHigh(priorityId))
        {
            throw new ProvisionalTicketRequiresCriticalOrHighException(priorityId);
        }

        var ticket = CreateCore(ticketNumber, departmentId, categoryId, priorityId, requestSummary, createdAtUtc);
        ticket.VerificationStatus = CrmVerificationStatus.PendingCrmVerification;

        // Not yet SLA-clocked (FR-TKT-09) — the clock has nothing to run
        // against until a unit/contact is actually resolved; SLA
        // due-date computation itself remains out of scope for this
        // increment regardless (SLA and Escalation module, later).
        ticket.SlaState = SlaState.Paused;
        return ticket;
    }

    private static Ticket CreateCore(
        string ticketNumber, int departmentId, int categoryId, byte priorityId, string requestSummary, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(ticketNumber))
        {
            throw new ArgumentException("TicketNumber is required.", nameof(ticketNumber));
        }

        if (string.IsNullOrWhiteSpace(requestSummary))
        {
            throw new ArgumentException("RequestSummary is required.", nameof(requestSummary));
        }

        return new Ticket
        {
            TicketNumber = ticketNumber,
            // Write-once (MVP-Data-Dictionary.md §2.3/§2.10) — no method on
            // this class ever changes OriginatingDepartmentId after
            // construction; only CurrentDepartmentId moves, and only via a
            // transfer operation this increment does not implement.
            OriginatingDepartmentId = departmentId,
            CurrentDepartmentId = departmentId,
            CategoryId = categoryId,
            PriorityId = priorityId,
            RequestSummary = requestSummary,
            CreatedAtUtc = createdAtUtc,
            TicketStatus = TicketStatus.Open,
            EscalationLevel = EscalationLevel.None,
            ReopenCount = 0
        };
    }

    /// <summary>
    /// Reconciles a provisional ticket once the CRM is reachable again and
    /// the requester has actually been verified (ISSUE-006's "reconciled
    /// once CRM returns"). Not called by any endpoint in this increment —
    /// implemented and unit-tested now so the invariant is real and ready
    /// for the next increment, the same forward-building pattern already
    /// used for <c>VerificationSession.Consume()</c>.
    /// </summary>
    public void ReconcileVerification(int unitReferenceId, int contactReferenceId)
    {
        if (VerificationStatus != CrmVerificationStatus.PendingCrmVerification)
        {
            throw new TicketNotPendingCrmVerificationException(TicketId, VerificationStatus);
        }

        UnitReferenceId = unitReferenceId;
        ContactReferenceId = contactReferenceId;
        VerificationStatus = CrmVerificationStatus.Verified;
        SlaState = SlaState.Running;
    }
}
