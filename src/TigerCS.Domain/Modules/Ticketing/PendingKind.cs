namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// Why a ticket is pending — the structured distinction the Workflow/SLA
/// phase requires on top of the existing <see cref="TicketStatus"/> values.
/// Values deliberately mirror the two existing pending statuses 1:1 (no new
/// status is introduced): <see cref="Customer"/> accompanies
/// <see cref="TicketStatus.PendingCustomer"/>,
/// <see cref="InternalOrThirdParty"/> accompanies
/// <see cref="TicketStatus.PendingThirdParty"/>. Whether either kind pauses
/// the SLA clock remains a pending business decision
/// (docs/Workflow-SLA-Configuration-Phase1.md §5) — this enum only records
/// the why, never a pause.
/// </summary>
public enum PendingKind : byte
{
    /// <summary>Waiting on the customer — missing documents, pending payment, awaiting a customer response.</summary>
    Customer = 1,

    /// <summary>Waiting on an internal department (e.g. Accounting approval, Maintenance) or an external party.</summary>
    InternalOrThirdParty = 2
}
