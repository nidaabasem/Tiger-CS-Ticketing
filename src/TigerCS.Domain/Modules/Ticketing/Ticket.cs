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
/// root and its five independent lifecycle dimensions (ADR-0008): creation,
/// assignment/transfer, the status sub-machine, resolve/close, and — added
/// by the SLA and Escalation increment — first-response recording, the
/// <see cref="EscalationLevel"/> dimension, and the <see cref="SlaState"/>
/// projection of the two independent SLA clocks. Priority change
/// (MVP-Implementation-Backlog.md S-14) is still not exposed by any method
/// below.
///
/// <para>
/// <b>UnitReferenceId/ContactReferenceId/RequesterSnapshot are optional —
/// a customer match is enrichment, never a Ticket creation gate.</b>
/// Customer lookup (CRM/PACT/Tasleeh, <c>CustomerLookupAppService</c>) is
/// enrichment/identification, not a promotion gate: whether the lookup finds
/// a match, finds nothing, or a source fails to answer, ticket creation
/// proceeds the same way (<see cref="CreateUnverified"/>). Only when the
/// caller already has a resolved local unit/contact pair — because the agent
/// selected a lookup match — is <see cref="CreateVerified"/> used instead,
/// and both fields are populated together, never one without the other. A
/// ticket that started unverified is not stuck that way: it may later be
/// enriched or linked once customer/unit information becomes available,
/// via <see cref="ReconcileVerification"/>.
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

    /// <summary>
    /// Workflow/SLA Configuration phase 2 — which configured
    /// <c>RequestType</c> this ticket follows, resolving its workflow
    /// template, capabilities, assignment rule, and request-type SLA layer.
    /// Nullable on purpose: tickets created before this phase (and callers
    /// that don't yet classify) carry null and keep the exact pre-phase-2
    /// behavior — workflow capabilities are only enforced when a request
    /// type is present, never guessed. Set once at creation via
    /// <see cref="ClassifyRequestType"/>; validation that it belongs to the
    /// ticket's department is the creating application service's job.
    /// </summary>
    public int? RequestTypeId { get; private set; }

    /// <summary>
    /// Business-rule change: the real CRM Buyer Lookup match the agent
    /// selected (<c>GET /api/crm/buyers</c> — phone search only, never a
    /// Unit/Project search). A different identifier space from
    /// <see cref="UnitReferenceId"/>/<see cref="ContactReferenceId"/> (that
    /// pair is the older CRM-unit-number cache, ICrmGateway — see that
    /// interface's remarks); the four CRM Buyer ids are always set together
    /// or not at all, mirroring the Unit/Contact pair's own invariant. Set
    /// only when the agent explicitly selected one of the matched Buyer's
    /// eligible (Sold/Contract) units — CRM never auto-selects one.
    /// </summary>
    public int? CrmBuyerCustomerId { get; private set; }
    public int? CrmBuyerLeadId { get; private set; }
    public int? CrmBuyerUnitId { get; private set; }
    public int? CrmBuyerProjectId { get; private set; }

    /// <summary>Immutable, ticket-time snapshot of the selected CRM Buyer match's display text — never re-read from CRM afterward, same ADR-0007 reasoning as <see cref="TicketRequesterSnapshot"/>.</summary>
    public string? CrmBuyerCustomerName { get; private set; }
    public string? CrmBuyerProjectName { get; private set; }
    public string? CrmBuyerUnitNumber { get; private set; }

    /// <summary>
    /// Business-rule change: when CRM Buyer Lookup found no match (or CRM was
    /// unavailable), the agent manually enters Project and Unit Number
    /// instead — both required together, and never used to run another CRM
    /// lookup (CRM is searched by phone number only). Mutually exclusive with
    /// the CrmBuyer* fields above: a ticket carries one or the other, never
    /// both.
    /// </summary>
    public string? ManualProjectName { get; private set; }
    public string? ManualUnitNumber { get; private set; }

    /// <summary>
    /// Which external customer-lookup source verified the customer identity
    /// this ticket was created against, when it was not the real CRM Buyer
    /// Lookup — e.g. "Pact" (a <c>CustomerLookupSource</c> name, stored as a
    /// string so future sources need no schema change). A matched PACT/
    /// Tasleeh customer IS verified — against that source — even though no
    /// local UnitReference/ContactReference exists for it; what stays
    /// CRM-scoped is <see cref="VerificationStatus"/> (see
    /// <see cref="CreateFromExternalLookup"/>'s remarks). Null for CRM Buyer
    /// tickets (their source is the CrmBuyer* fields themselves) and for
    /// plain manual entry.
    /// </summary>
    public string? CustomerVerificationSource { get; private set; }

    /// <summary>
    /// The source's own identifier for the verified customer (for PACT, its
    /// tenantID) — an external identifier only, stored for auditability,
    /// reconciliation, and finding this customer's other tickets; never a
    /// foreign key, and never resolved through a local cache table (none
    /// exists for these sources). Travels with
    /// <see cref="CustomerVerificationSource"/>, never alone.
    /// </summary>
    public string? ExternalCustomerId { get; private set; }

    /// <summary>The source's own identifier for the selected unit (for PACT, its unitID) — same external-identifier-only discipline as <see cref="ExternalCustomerId"/>.</summary>
    public string? ExternalUnitId { get; private set; }

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

    /// <summary>The customer-match path: the caller already has a resolved local unit/contact pair (the agent selected a CRM lookup match). Fully verified from the moment it exists.</summary>
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

    /// <summary>
    /// The default path whenever no resolved unit/contact pair is available
    /// at creation time — whether the intake is not unit-related, the
    /// customer lookup found no match, a lookup source failed to answer, or
    /// the agent simply proceeded without selecting one. Customer lookup is
    /// enrichment, never a Ticket creation gate (see this type's remarks), so
    /// none of those reasons changes this factory's behavior: no Unit/Contact
    /// reference and no requester snapshot exist yet, and the SLA clock still
    /// starts immediately — nothing here is pending on an external system.
    /// <see cref="VerificationStatus"/> stays <see cref="CrmVerificationStatus.Unverified"/>
    /// until (and unless) <see cref="ReconcileVerification"/> later links a
    /// unit/contact once one becomes available.
    /// </summary>
    /// <param name="ticketNumber">The generated, unique ticket number.</param>
    /// <param name="departmentId">The originating (and initial current) department.</param>
    /// <param name="categoryId">The selected, active Ticket Category.</param>
    /// <param name="priorityId">1=Critical, 2=High, 3=Medium, 4=Low.</param>
    /// <param name="requestSummary">The caller's request, in the agent's words.</param>
    /// <param name="createdAtUtc">Creation time, in UTC — also the SLA clock-start event.</param>
    /// <param name="manualProjectName">
    /// Business-rule change: required together with <paramref name="manualUnitNumber"/>
    /// when the CRM Buyer Lookup found no match for the intake's phone number
    /// (or CRM was unavailable) — the caller enforces the pairing before this
    /// factory runs (<c>TicketCreationAppService.CreateAsync</c>), same
    /// division of responsibility as <see cref="CreateVerifiedFromCrmBuyer"/>.
    /// </param>
    /// <param name="manualUnitNumber">See <paramref name="manualProjectName"/>.</param>
    public static Ticket CreateUnverified(
        string ticketNumber,
        int departmentId,
        int categoryId,
        byte priorityId,
        string requestSummary,
        DateTime createdAtUtc,
        string? manualProjectName = null,
        string? manualUnitNumber = null)
    {
        var ticket = CreateCore(ticketNumber, departmentId, categoryId, priorityId, requestSummary, createdAtUtc);
        ticket.VerificationStatus = CrmVerificationStatus.Unverified;
        ticket.SlaState = SlaState.Running;
        ticket.ManualProjectName = manualProjectName;
        ticket.ManualUnitNumber = manualUnitNumber;
        return ticket;
    }

    /// <summary>
    /// The real CRM Buyer Lookup match path: the agent explicitly selected
    /// one of a matched Buyer's eligible (Sold/Contract) units from
    /// <c>GET /api/crm/buyers</c>'s results. A different identifier space
    /// from <see cref="CreateVerified"/>'s Unit/Contact reference pair (that
    /// factory is the older CRM-unit-number cache path, ICrmGateway) — this
    /// one never touches <see cref="UnitReferenceId"/>/<see cref="ContactReferenceId"/>.
    /// Fully verified from the moment it exists, same as <see cref="CreateVerified"/>:
    /// a real CRM Buyer match is exactly the kind of confirmed customer
    /// identification <see cref="CrmVerificationStatus.Verified"/> means.
    /// </summary>
    public static Ticket CreateVerifiedFromCrmBuyer(
        string ticketNumber,
        int departmentId,
        int crmBuyerCustomerId,
        int crmBuyerLeadId,
        int crmBuyerUnitId,
        int crmBuyerProjectId,
        string? crmBuyerCustomerName,
        string? crmBuyerProjectName,
        string? crmBuyerUnitNumber,
        int categoryId,
        byte priorityId,
        string requestSummary,
        DateTime createdAtUtc)
    {
        var ticket = CreateCore(ticketNumber, departmentId, categoryId, priorityId, requestSummary, createdAtUtc);
        ticket.CrmBuyerCustomerId = crmBuyerCustomerId;
        ticket.CrmBuyerLeadId = crmBuyerLeadId;
        ticket.CrmBuyerUnitId = crmBuyerUnitId;
        ticket.CrmBuyerProjectId = crmBuyerProjectId;
        ticket.CrmBuyerCustomerName = crmBuyerCustomerName;
        ticket.CrmBuyerProjectName = crmBuyerProjectName;
        ticket.CrmBuyerUnitNumber = crmBuyerUnitNumber;
        ticket.VerificationStatus = CrmVerificationStatus.Verified;
        ticket.SlaState = SlaState.Running;
        return ticket;
    }

    /// <summary>
    /// The external-lookup path: the agent explicitly selected one customer's
    /// one unit from a matched PACT/Tasleeh result in the department-aware
    /// customer lookup. The customer identity IS verified — against that
    /// external source — and this factory records exactly which source and
    /// which of its own customer/unit identifiers
    /// (<see cref="CustomerVerificationSource"/>/<see cref="ExternalCustomerId"/>/
    /// <see cref="ExternalUnitId"/> — external identifiers only, never
    /// foreign keys and never a local cache row), alongside the same
    /// human-readable Project/Unit snapshot the manual path stores.
    ///
    /// <para>
    /// <see cref="VerificationStatus"/> deliberately stays
    /// <see cref="CrmVerificationStatus.Unverified"/>: that enum is the
    /// CRM-named concept the rest of the system keys on (reconciliation
    /// still may link a local CRM Unit/Contact reference later; a PACT match
    /// carries no such pair), so external verification is expressed by the
    /// source fields — display and reporting derive "Verified via PACT" from
    /// them — rather than by widening a CRM-scoped status.
    /// </para>
    /// </summary>
    public static Ticket CreateFromExternalLookup(
        string ticketNumber,
        int departmentId,
        string customerVerificationSource,
        string? externalCustomerId,
        string? externalUnitId,
        string? manualProjectName,
        string? manualUnitNumber,
        int categoryId,
        byte priorityId,
        string requestSummary,
        DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(customerVerificationSource))
        {
            throw new ArgumentException(
                "CustomerVerificationSource is required — external identifiers never travel without their source.",
                nameof(customerVerificationSource));
        }

        var ticket = CreateUnverified(
            ticketNumber, departmentId, categoryId, priorityId, requestSummary, createdAtUtc,
            manualProjectName, manualUnitNumber);
        ticket.CustomerVerificationSource = customerVerificationSource;
        ticket.ExternalCustomerId = externalCustomerId;
        ticket.ExternalUnitId = externalUnitId;
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
    /// Links a unit/contact pair onto a ticket that did not have one at
    /// creation — the "later enrichment" story for a ticket that started
    /// <see cref="CrmVerificationStatus.Unverified"/> (business-rule change:
    /// customer lookup no longer gates creation, so this is the normal way a
    /// ticket picks up a customer match afterward, not a rare recovery path).
    ///
    /// <para>
    /// <b>Deliberately a bare state transition, not full orchestration —
    /// same division of responsibility as <see cref="CreateVerified"/>.</b>
    /// This method does not itself create a <c>TicketRequesterSnapshot</c>;
    /// sourcing a genuinely resolved <c>unitReferenceId</c>/
    /// <c>contactReferenceId</c> pair and constructing the resulting
    /// <c>TicketRequesterSnapshot</c> is the calling application service's
    /// job, done alongside this call in one transaction — see
    /// <c>TicketCreationAppService.CreateAsync</c> for the pattern this
    /// method's caller mirrors. A caller that invokes this with unvalidated
    /// IDs, or that skips writing the snapshot, produces a
    /// <see cref="CrmVerificationStatus.Verified"/> ticket that violates
    /// ADR-0007 — that correctness is this method's caller's responsibility,
    /// not something its own signature enforces.
    /// </para>
    /// </summary>
    public void ReconcileVerification(int unitReferenceId, int contactReferenceId)
    {
        if (VerificationStatus == CrmVerificationStatus.Verified)
        {
            throw new TicketAlreadyVerifiedException(TicketId);
        }

        UnitReferenceId = unitReferenceId;
        ContactReferenceId = contactReferenceId;
        VerificationStatus = CrmVerificationStatus.Verified;
    }

    /// <summary>
    /// Records which configured RequestType this ticket follows — write-once,
    /// at creation, by the creating application service (the same bare
    /// state-transition division of responsibility as
    /// <see cref="ReconcileVerification"/>: department/active validation
    /// happens before this is called).
    /// </summary>
    public void ClassifyRequestType(int requestTypeId)
    {
        if (RequestTypeId is not null)
        {
            throw new TicketRequestTypeAlreadySetException(TicketId, RequestTypeId.Value);
        }

        RequestTypeId = requestTypeId;
    }

    /// <summary>MVP-API-Contracts.md §3.5 / §2.12 — sets the current owner and appends a superseding <see cref="TicketAssignment"/> row is the caller's job (this method only updates the ticket's own denormalized pointer).</summary>
    public void AssignTo(Guid employeeId)
    {
        EnsureNotClosed();

        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("AssignedEmployeeId is required.", nameof(employeeId));
        }

        CurrentOwnerEmployeeId = employeeId;
    }

    /// <summary>MVP-API-Contracts.md §3.6 — moves CurrentDepartmentId, leaves OriginatingDepartmentId untouched (write-once, MVP-ERD.md §2.3), and clears the current owner: the receiving department must explicitly claim/assign it.</summary>
    public void TransferToDepartment(int targetDepartmentId)
    {
        EnsureNotClosed();

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
        EnsureNotClosed();

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
        EnsureNotClosed();

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
        // Closing an already-Closed ticket is closed-ticket immutability
        // (PR correction), not "not yet resolved" — a genuinely different
        // condition from the check below, which is why this method checks
        // EnsureNotClosed() explicitly first rather than letting the
        // `!= Resolved` branch (also true for Closed) catch it implicitly.
        EnsureNotClosed();

        if (TicketStatus != TicketStatus.Resolved)
        {
            throw new TicketNotYetResolvedException(TicketId);
        }

        TicketStatus = TicketStatus.Closed;
    }

    /// <summary>
    /// FR-RES-04 / MVP-API-Contracts.md §3.11 — Reopen is a domain event,
    /// not a status value: a Resolved or Closed ticket returns to
    /// InProgress, <see cref="ReopenCount"/> increments, and the live
    /// <see cref="ResolutionOutcome"/>/<see cref="DuplicateOfTicketId"/>
    /// return to unset (Solution-Analysis.md §5's "the field returns to
    /// unset until the ticket is resolved/closed again"). The prior outcome
    /// is preserved as history on its TicketResolutions row — flipping that
    /// row's IsCurrent, writing status history and audit are the calling
    /// application service's job, done in the same transaction (same bare
    /// state-transition division of responsibility as <see cref="Resolve"/>/
    /// <see cref="Close"/>).
    ///
    /// <para>
    /// Deliberately <i>not</i> gated on <see cref="EnsureNotClosed"/>:
    /// Reopen is the single sanctioned exit from closed-ticket immutability
    /// (System-Architecture.md's <c>Closed → InProgress: Reopen (within
    /// window)</c> transition). The reopen window itself (ISSUE-011 — 7
    /// days, configurable) is a business-rule check owned by the
    /// application service; this method carries no clock.
    /// </para>
    /// </summary>
    public void Reopen()
    {
        if (TicketStatus is not (TicketStatus.Resolved or TicketStatus.Closed))
        {
            throw new TicketNotEligibleForReopenException(TicketId, TicketStatus);
        }

        TicketStatus = TicketStatus.InProgress;
        ResolutionOutcome = null;
        DuplicateOfTicketId = null;
        ReopenCount++;
    }

    /// <summary>
    /// Records that the <b>automated</b> acknowledgement (FR-NOT-01) was
    /// delivered.
    ///
    /// <para>
    /// <b>This is not, and can never become, a First Response event.</b>
    /// FR-SLA-05/ISSUE-019 exist precisely because an acknowledgement fires
    /// within seconds of creation and would otherwise "satisfy" every
    /// first-response target automatically, making the KPI meaningless. This
    /// method therefore writes <see cref="AcknowledgementSentAtUtc"/> and
    /// touches nothing else — not <see cref="FirstHumanResponseAtUtc"/>, not
    /// <see cref="SlaState"/>, not <see cref="TicketStatus"/>. The two
    /// timestamps are separate columns (MVP-Data-Dictionary.md §2.10) read by
    /// separate code paths, and <c>SlaBreachProcessor</c> resolves First
    /// Response from <see cref="FirstHumanResponseAtUtc"/> alone.
    /// </para>
    ///
    /// <para>
    /// Write-once, and deliberately <i>not</i> gated on
    /// <see cref="EnsureNotClosed"/>. Write-once is the delivery-side
    /// duplicate guard: a redelivered Outbox message reaching an
    /// already-acknowledged ticket is refused here rather than emailing the
    /// customer twice. The closed-ticket gate is omitted because an
    /// acknowledgement is an asynchronous consequence of creation, and a
    /// ticket created and closed within the dispatcher's polling interval
    /// must still be able to record what was already sent — refusing would
    /// lose the record of a delivery that genuinely happened, which is worse
    /// than recording it.
    /// </para>
    /// </summary>
    public void RecordAcknowledgementSent(DateTime sentAtUtc)
    {
        if (AcknowledgementSentAtUtc is { } already)
        {
            throw new AcknowledgementAlreadySentException(TicketId, already);
        }

        AcknowledgementSentAtUtc = sentAtUtc;
    }

    /// <summary>
    /// ISSUE-019 / MVP-API-Contracts.md §5.2 — records the first genuine,
    /// human-authored response to the customer. Write-once at the ticket
    /// level (MVP-ERD.md §2.10): a second call throws rather than
    /// overwriting, because the First Response SLA is measured against the
    /// <i>first</i> engagement and a later correction would silently move a
    /// contractual measurement.
    ///
    /// <para>
    /// The automated acknowledgement never reaches this method, on any
    /// channel — that is <see cref="AcknowledgementSentAtUtc"/>'s separate
    /// job, and keeping the two apart is the whole point of ISSUE-019.
    /// </para>
    ///
    /// <para>
    /// <paramref name="occurredAtUtc"/> may precede
    /// <see cref="CreatedAtUtc"/>: SLA-Architecture.md §16's Example E has a
    /// Genesys call answered at 08:58 satisfying a ticket created moments
    /// later at 09:00, and the actual moment of human engagement is the
    /// correct value there. No Genesys adapter ships in this increment; the
    /// method simply does not forbid the case its own approved example
    /// requires.
    /// </para>
    /// </summary>
    public void RecordFirstHumanResponse(DateTime occurredAtUtc)
    {
        EnsureNotClosed();

        if (FirstHumanResponseAtUtc is { } already)
        {
            throw new FirstResponseAlreadyRecordedException(TicketId, already);
        }

        FirstHumanResponseAtUtc = occurredAtUtc;
    }

    /// <summary>
    /// Advances the <see cref="EscalationLevel"/> dimension — and nothing
    /// else.
    ///
    /// <para>
    /// <b>Escalation is a separate dimension from <see cref="TicketStatus"/></b>
    /// (ADR-0008, Solution-Analysis.md §5.3/§7.6: "escalating a ticket never
    /// removes it from active work"). This method deliberately does not
    /// touch <see cref="TicketStatus"/>, <see cref="ResolutionOutcome"/> or
    /// <see cref="SlaState"/>, so an escalated ticket stays exactly as Open
    /// or InProgress as it was.
    /// </para>
    ///
    /// <para>
    /// The level only ever rises. ADR-0011 models it as a state rather than
    /// a counter, so an automatic Level 2 raised by a later breach must not
    /// pull a ticket back down from a Level 4 a CS Manager already set.
    /// </para>
    /// </summary>
    public void RaiseEscalationLevel(EscalationLevel newLevel)
    {
        EnsureNotClosed();

        if (newLevel <= EscalationLevel)
        {
            throw new EscalationLevelCannotBeLoweredException(TicketId, EscalationLevel, newLevel);
        }

        EscalationLevel = newLevel;
    }

    /// <summary>
    /// Projects a recorded SLA breach onto the ticket's own
    /// <see cref="SlaState"/> dimension (SLA-Architecture.md §11).
    ///
    /// <para>
    /// The two clocks breach independently and are tracked independently on
    /// <c>TicketSlaInstances</c> (ADR-0009); <see cref="SlaState"/> is a
    /// single column, so it carries the ticket-level summary: breached if
    /// <i>either</i> clock is. Sticky, matching the breach flags' own
    /// immutability rule (MVP-ERD.md §2.15) — nothing moves this back to
    /// Running or Met.
    /// </para>
    /// </summary>
    public void MarkSlaBreached() => SlaState = SlaState.Breached;

    /// <summary>
    /// Marks the SLA met once the resolution landed within its deadline.
    /// A breach already recorded wins and is never downgraded to Met, so a
    /// ticket that missed its First Response target and then resolved on
    /// time still reports Breached.
    /// </summary>
    public void MarkSlaMet()
    {
        if (SlaState != SlaState.Breached)
        {
            SlaState = SlaState.Met;
        }
    }

    /// <summary>Closed-ticket immutability (PR correction): every mutating method above calls this first — a Closed ticket accepts no further Assign/Transfer/ChangeStatus/Resolve/Close.</summary>
    private void EnsureNotClosed()
    {
        if (TicketStatus == TicketStatus.Closed)
        {
            throw new TicketClosedException(TicketId);
        }
    }
}
