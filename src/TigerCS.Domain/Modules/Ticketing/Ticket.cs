using TigerCS.Domain.Modules.SlaAndEscalation;

// Alias needed because this type's own ResolutionOutcome *property* (byte?)
// shadows the ResolutionOutcome *enum* by simple name inside instance
// methods (C# prefers the instance member over the type in that position) —
// this gives instance methods below an unambiguous way to reference the
// enum's members.
using ResolutionOutcomeValue = TigerCS.Domain.Modules.Ticketing.ResolutionOutcome;

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

    /// <summary>
    /// MVP-Data-Dictionary.md §2.10 — optimistic concurrency token. Deferred
    /// by the ticket-creation increment ("no endpoint in this increment
    /// mutates an existing Ticket") to the increment that adds assignment/
    /// transfer/status-change — this one. EF Core maps this as SQL Server
    /// `rowversion`; never set from application code.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];

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
    ///
    /// <para>
    /// <b>Deliberately a bare state transition, not full orchestration —
    /// same division of responsibility as <see cref="CreateVerified"/>.</b>
    /// This method does not itself create a <c>TicketRequesterSnapshot</c>,
    /// and takes raw IDs rather than a <c>VerificationSession</c>, exactly
    /// as <see cref="CreateVerified"/> also takes raw
    /// <c>unitReferenceId</c>/<c>contactReferenceId</c> rather than a
    /// session object — in both cases, sourcing those IDs from a genuinely
    /// confirmed, single-use, owned <c>VerificationSession</c> (never a
    /// caller-supplied raw pair) and constructing the resulting
    /// <c>TicketRequesterSnapshot</c> is the calling application service's
    /// job, done alongside this call in one transaction — see
    /// <c>TicketCreationAppService.CreateFromVerificationSessionAsync</c>
    /// for the pattern this method's future caller must mirror. A caller
    /// that invokes this with unvalidated IDs, or that skips writing the
    /// snapshot, produces a <see cref="CrmVerificationStatus.Verified"/>
    /// ticket that violates ADR-0007 — that correctness is this method's
    /// caller's responsibility, not something its own signature enforces.
    /// </para>
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

    /// <summary>MVP-API-Contracts.md §3.5 / §2.12 — sets the current owner and appends a superseding <see cref="TicketAssignment"/> row is the caller's job (this method only updates the ticket's own denormalized pointer).</summary>
    public void AssignTo(Guid employeeId)
    {
        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("AssignedEmployeeId is required.", nameof(employeeId));
        }

        CurrentOwnerEmployeeId = employeeId;
    }

    /// <summary>MVP-API-Contracts.md §3.6 — moves CurrentDepartmentId, leaves OriginatingDepartmentId untouched (write-once, MVP-ERD.md §2.3), and clears the current owner: the receiving department must explicitly claim/assign it.</summary>
    public void TransferToDepartment(int targetDepartmentId)
    {
        if (targetDepartmentId == CurrentDepartmentId)
        {
            throw new TicketAlreadyInTargetDepartmentException(TicketId, targetDepartmentId);
        }

        CurrentDepartmentId = targetDepartmentId;
        CurrentOwnerEmployeeId = null;
    }

    /// <summary>
    /// Solution-Analysis.md §5.6's TicketStatus transition table, restricted
    /// to the "work" sub-machine this method owns: Open→InProgress
    /// (requires an already-assigned owner) and InProgress↔PendingCustomer/
    /// PendingThirdParty (pivoting through InProgress, not directly between
    /// the two Pending states). Resolved/Closed are reached only via
    /// <see cref="Resolve"/>/<see cref="Close"/> — deliberately distinct
    /// operations, not reachable through this method (per this increment's
    /// scope: Resolve/Close stay separate actions).
    /// </summary>
    public void ChangeStatus(TicketStatus newStatus)
    {
        var isAllowed = (TicketStatus, newStatus) switch
        {
            (TicketStatus.Open, TicketStatus.InProgress) => true,
            (TicketStatus.InProgress, TicketStatus.PendingCustomer) => true,
            (TicketStatus.InProgress, TicketStatus.PendingThirdParty) => true,
            (TicketStatus.PendingCustomer, TicketStatus.InProgress) => true,
            (TicketStatus.PendingThirdParty, TicketStatus.InProgress) => true,
            _ => false
        };

        if (!isAllowed)
        {
            throw new InvalidTicketStatusTransitionException(TicketId, TicketStatus, newStatus);
        }

        if (newStatus == TicketStatus.InProgress && TicketStatus == TicketStatus.Open && CurrentOwnerEmployeeId is null)
        {
            throw new TicketNotAssignedException(TicketId);
        }

        TicketStatus = newStatus;
    }

    /// <summary>
    /// MVP-API-Contracts.md §3.9 / Solution-Analysis.md §5.6 — marks the
    /// underlying work done. Valid only from InProgress/PendingCustomer/
    /// PendingThirdParty (never directly from Open, never twice). Does not
    /// itself create the <see cref="TicketResolution"/> row or write audit/
    /// history — mirrors <see cref="CreateVerified"/>'s "bare state
    /// transition" division of responsibility; the calling application
    /// service does both in the same transaction.
    /// </summary>
    public void Resolve(ResolutionOutcomeValue outcome, long? duplicateOfTicketId)
    {
        if (TicketStatus is not (TicketStatus.InProgress or TicketStatus.PendingCustomer or TicketStatus.PendingThirdParty))
        {
            throw new TicketNotEligibleForResolutionException(TicketId, TicketStatus);
        }

        TicketStatus = TicketStatus.Resolved;
        ResolutionOutcome = (byte)outcome;
        DuplicateOfTicketId = outcome == ResolutionOutcomeValue.Duplicate ? duplicateOfTicketId : null;
    }

    /// <summary>MVP-API-Contracts.md §3.10 — the final, CS-layer-only close, distinct from Resolve. Requires a current TicketResolutions row (enforced by the caller — this method only enforces the TicketStatus precondition).</summary>
    public void Close()
    {
        if (TicketStatus != TicketStatus.Resolved)
        {
            throw new TicketNotYetResolvedException(TicketId);
        }

        TicketStatus = TicketStatus.Closed;
    }
}
