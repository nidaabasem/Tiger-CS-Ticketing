namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// The single rule for a CRM sale that counts as a unit's owner (Collections lists, the CRM owner feed, phone lookup and ticket creation): a buyer's lead whose status is
/// <b>Sold</b> or <b>Contract</b> and is not cancelled.
/// <para>
/// The status NAME that CRM sends decides. The numeric lead-status values are NOT verified anywhere in this repository (the only definition is the test stub
/// <c>docs/Genesys/crm-insertion/harness/Stubs.cs</c>: Contract = 4, Sold = 8, Cancelled = 9, written for the CRM insertion harness; CRM's own enum and whether
/// <c>LeadStatus.Contract</c> exists are open question A7 in <c>docs/Genesys/CRM-GetUnitDetails-Field-Mapping.md</c>), so a number is consulted only when CRM sent no name at all, and
/// then only the configured fallback list. A cancelled lead is refused by name and its number is deliberately not hard-coded.
/// </para>
/// </summary>
public static class CrmSaleEligibility
{
    public static bool IsEligible(int leadStatus, string? leadStatusName, IReadOnlyCollection<int> fallbackStatuses)
    {
        var name = leadStatusName?.Trim();
        if (!string.IsNullOrEmpty(name))
            return !name.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                && (name.Equals("Sold", StringComparison.OrdinalIgnoreCase) || name.Equals("Contract", StringComparison.OrdinalIgnoreCase));
        return fallbackStatuses.Contains(leadStatus);
    }
}
