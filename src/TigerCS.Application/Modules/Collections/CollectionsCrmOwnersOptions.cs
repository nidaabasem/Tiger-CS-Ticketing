namespace TigerCS.Application.Modules.Collections;

/// <summary>
/// The bulk CRM owner feed (<c>CollectionsSource:CrmOwners</c>): who holds an eligible sale of each unit. Loaded in the background into
/// <c>dbo.CollectionsCrmUnitOwner</c> and joined to the receivables in SQL, so no page ever calls CRM once per row.
/// </summary>
public sealed class CollectionsCrmOwnersOptions
{
    public const string SectionName = "CollectionsSource:CrmOwners";

    /// <summary>Off until CRM publishes <c>GET /TicketingSystem/GetUnitOwners</c> (docs/Collections/CRM-GetUnitOwners-Contract.md). Disabled = PACT contact data only.</summary>
    public bool Enabled { get; set; }

    /// <summary>Hangfire cron of the reload (server local time). Default: hourly.</summary>
    public string RefreshCron { get; set; } = "15 * * * *";

    public bool RefreshOnStartup { get; set; } = true;

    public int PageSize { get; set; } = 1000;

    /// <summary>Safety bound: a feed that never ends is a fault, not data.</summary>
    public int MaxPages { get; set; } = 500;

    /// <summary>
    /// FALLBACK ONLY. A sale counts when CRM's status NAME is Sold or Contract and not cancelled (<see cref="TigerCS.Domain.Modules.Collections.CrmSaleEligibility"/>); these numbers
    /// (<b>4 = Contract</b>, <b>8 = Sold</b>, observed in production responses, NOT verified against CRM's own LeadStatus enum) are consulted only for a row that arrives with no status
    /// name. A cancelled lead is refused by name; its number is deliberately not hard-coded.
    /// </summary>
    public List<int> EligibleLeadStatuses { get; set; } = [4, 8];

    /// <summary>CRM's <c>CustomerType</c> of a buyer (<c>CustomerType.Buyer</c> = 1, the same rule as GetBuyerByPhone). Other relations (tenants, representatives) are never owners.</summary>
    public int BuyerCustomerType { get; set; } = 1;
}
