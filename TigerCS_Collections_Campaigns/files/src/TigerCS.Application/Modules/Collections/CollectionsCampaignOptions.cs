namespace TigerCS.Application.Modules.Collections;

public sealed class CollectionsCampaignOptions
{
    public const string SectionName = "Collections:Campaigns";
    /// <summary>Enable only after reconciling remaining amounts AND unit allocation against actual PACT records.
    /// A V2 procedure name alone does not prove the reconciliation succeeded.</summary>
    public bool FinancialSourceValidated { get; set; }
    /// <summary>Separate release switch for client-facing legal notices. Preview and internal review remain available.</summary>
    public bool LegalNoticeExportEnabled { get; set; }
    public int MaxExportRows { get; set; } = 5000;
}
