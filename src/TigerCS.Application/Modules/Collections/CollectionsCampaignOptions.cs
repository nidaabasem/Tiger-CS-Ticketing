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

    /// <summary>The report emailed to the Collections administrator(s) after an approved send finishes (<c>Collections:Campaigns:AdminNotification</c>).</summary>
    public CollectionsAdminNotificationOptions AdminNotification { get; set; } = new();
}

/// <summary>
/// Who receives the approved-customer list and the actual Genesys results. Nothing is hardcoded: recipients come only from configuration
/// (<c>appsettings.json</c>, <c>appsettings.{Environment}.json</c> or environment variables such as
/// <c>Collections__Campaigns__AdminNotification__Recipients__0</c>), so they change without a rebuild.
/// </summary>
public sealed class CollectionsAdminNotificationOptions
{
    public const string CsvFormat = "Csv";

    /// <summary>When true (default) every finished send is reported; a missing or invalid recipient list is then shown as NotConfigured, never as sent.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>One or more addresses. An entry may itself hold several addresses separated by ';' or ',' (convenient in an environment variable).</summary>
    public string[] Recipients { get; set; } = [];

    /// <summary>Attach the approved customer list with the per-customer result.</summary>
    public bool AttachCustomerList { get; set; } = true;

    /// <summary>Only <c>Csv</c> is supported; any other value is reported as a configuration problem instead of being ignored.</summary>
    public string AttachmentFormat { get; set; } = CsvFormat;

    public bool IsSupportedFormat => string.Equals(AttachmentFormat?.Trim(), CsvFormat, StringComparison.OrdinalIgnoreCase);

    /// <summary>The distinct, syntactically valid addresses (case-insensitive) and how many configured entries were not valid.</summary>
    public (IReadOnlyList<string> Valid, int Invalid) ResolveRecipients()
    {
        var valid = new List<string>();
        var invalid = 0;
        foreach (var entry in Recipients ?? [])
            foreach (var part in (entry ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var address = TigerCS.Application.Modules.Notifications.Services.CustomerEmailAddress.Normalize(part);
                if (address is not null && TigerCS.Application.Modules.Notifications.Services.CustomerEmailAddress.IsValid(address))
                {
                    if (!valid.Contains(address, StringComparer.OrdinalIgnoreCase)) valid.Add(address);
                }
                else invalid++;
            }
        return (valid, invalid);
    }
}
