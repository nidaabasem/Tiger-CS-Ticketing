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

    /// <summary>
    /// How old the last successful load may be before the CRM contact data counts as out of date (the refresh runs hourly by default). Past it the pages say so and every unit is flagged
    /// <c>CrmUnavailable</c> instead of silently showing PACT-only contacts as if CRM had nothing.
    /// </summary>
    public int MaxAgeMinutes { get; set; } = 360;

    /// <summary>Safety bound: a feed that never ends is a fault, not data.</summary>
    public int MaxPages { get; set; } = 500;

    /// <summary>
    /// CRM's LeadStatus values, confirmed by the CRM owners: <b>Sold = 8</b>, <b>Contract = 9</b>; NOT eligible: <b>Approved = 4</b>, <b>Cancelled = 6</b>. A sale counts when its status NAME is Sold or
    /// Contract (<see cref="TigerCS.Domain.Modules.Collections.CrmSaleEligibility"/>); this list is consulted only for a row that arrives with no status name. Default <c>[8, 9]</c>.
    /// </summary>
    public List<int> EligibleLeadStatuses { get; set; } = [8, 9];

    /// <summary>CRM's <c>CustomerType</c> of a buyer (<c>CustomerType.Buyer</c> = 1, the same rule as GetBuyerByPhone). Other relations (tenants, representatives) are never owners.</summary>
    public int BuyerCustomerType { get; set; } = 1;
}
