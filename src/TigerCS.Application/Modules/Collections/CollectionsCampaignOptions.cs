namespace TigerCS.Application.Modules.Collections;

public sealed class CollectionsCampaignOptions
{
    public const string SectionName = "Collections:Campaigns";
    /// <summary>Enable only after reconciling remaining amounts AND unit allocation against actual PACT records.
    /// A V2 procedure name alone does not prove the reconciliation succeeded.</summary>
    public bool FinancialSourceValidated { get; set; }
    /// <summary>The <c>CollectionsSource:PactReceivables:ProcedureSuffix</c> that the reconciliation was performed against.
    /// The sign-off only counts while the configured procedure is the one that was reconciled; switching procedures
    /// (for example from the original to V2) requires a new reconciliation.</summary>
    public string ValidatedProcedureSuffix { get; set; } = "";
    /// <summary>Free-text reference to the reconciliation evidence (ticket, workbook, sign-off). Shown to reviewers; not a secret.</summary>
    public string? ReconciliationReference { get; set; }
    /// <summary>Legal Case records are internal Legal referrals under the approved policy. Dispatching them to a customer
    /// contact list needs an explicit business decision; false until Legal/Collections record it.</summary>
    public bool LegalCaseDispatchApproved { get; set; }
    /// <summary>Separate release switch for client-facing legal notices. Preview and internal review remain available.</summary>
    public bool LegalNoticeExportEnabled { get; set; }
    public int MaxExportRows { get; set; } = 5000;
}
