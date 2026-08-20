namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// Solution-Analysis.md §5.2 / MVP-Data-Dictionary.md §2.9. Shared value set
/// for both <see cref="IntakeRecord.CrmVerificationStatus"/> and
/// <see cref="Ticket.VerificationStatus"/> — the same three-state model
/// (System-Architecture.md line 182), not two independently-invented enums.
/// </summary>
public enum CrmVerificationStatus : byte
{
    /// <summary>No CRM match attempted/found yet.</summary>
    Unverified = 1,

    /// <summary>Provisional — awaiting CRM reconciliation (ISSUE-006's outage fallback, this pilot's only trigger).</summary>
    PendingCrmVerification = 2,

    /// <summary>CRM unit/contact match confirmed; snapshot captured (FR-VER-05, ADR-0007).</summary>
    Verified = 3
}
