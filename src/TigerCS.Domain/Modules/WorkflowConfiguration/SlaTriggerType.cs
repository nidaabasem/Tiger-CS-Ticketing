namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// When a request-type SLA clock starts. Not every SLA starts at ticket
/// creation: the Customer Service SLA document states Collections' Send
/// Receipts one-day SLA runs from Accounting approval, and Handover's 1–4
/// day approval duration runs from Customer Service approval — starting
/// those at creation would be wrong, so the trigger is configuration, not an
/// assumption.
///
/// <para>
/// Phase 1 stores the trigger; the clock-start plumbing that honors
/// non-creation triggers (approval events, prerequisite completion) arrives
/// with phases 3–4. The existing initial-period behavior (clock starts at
/// creation, ISSUE-001 Option C) is exactly <see cref="TicketCreated"/>.
/// </para>
/// </summary>
public enum SlaTriggerType : byte
{
    /// <summary>The existing default — the clock starts when the ticket is created (ISSUE-001 Option C).</summary>
    TicketCreated = 1,

    /// <summary>The clock starts when the ticket is assigned.</summary>
    Assigned = 2,

    /// <summary>The clock starts when the configured approval is received (Collections / Send Receipts: after Accounting approval).</summary>
    ApprovalReceived = 3,

    /// <summary>The clock starts when Customer Service approves (Handover: 1–4 days after Customer Service approval).</summary>
    CustomerServiceApproved = 4,

    /// <summary>The clock starts once prerequisites are satisfied (Registration / Register Unit: 1–3 days "when everything is OK").</summary>
    PrerequisitesCompleted = 5
}
