namespace TigerCS.Application.Modules.Collections;

/// <summary>Direct PACT report reads; independent of the per-customer EDSM APIs.</summary>
public sealed class PactReceivablesOptions
{
    public const string SectionName = "CollectionsSource:PactReceivables";
    public bool Enabled { get; set; }
    /// <summary>
    /// Optional suffix of the report procedures, e.g. "V2" selects dbo.p4AccountReceivablesV2 / dbo.p32AccountReceivablesV2
    /// (review drafts in docs/Collections/pact-sql; not deployed). Empty uses the deployed originals. Letters/digits only.
    /// </summary>
    public string ProcedureSuffix { get; set; } = "";
    public string ConnectionStringName { get; set; } = "PACTRPT";
    public int CommandTimeoutSeconds { get; set; } = 120;
    /// <summary>
    /// Whole-request budget (API → SQL). The two company procedures run concurrently, so the slower one may use up to
    /// <see cref="CommandTimeoutSeconds"/>; the shared deadline adds a margin so the SQL command timeout (reported as a
    /// SQL timeout) always fires before the budget token (reported as "timed out"). The Web client's HttpClient timeout
    /// must exceed this value or the browser-facing request expires first.
    /// </summary>
    public int RequestBudgetSeconds => Math.Clamp(CommandTimeoutSeconds, 1, 300) + 30;
    public int MaxSourceRows { get; set; } = 250000;
    public string Currency { get; set; } = "AED";
    /// <summary>Off by default: the list shows every apartment with a positive due or overdue amount.</summary>
    public bool ApplyLegacyExclusions { get; set; }
    /// <summary>Lower bound of the legacy booking/down-payment CRM query; required only when legacy exclusions are on.</summary>
    public DateTime? LegacyDownPaymentFromDate { get; set; }
    public string CrmConnectionStringName { get; set; } = "CrmDatabase";
    /// <summary>Set after copying the actual Helper.SEPCIAL_CASES list; an empty list must be intentional.</summary>
    public bool SpecialCasesConfigured { get; set; }
    public List<string> SpecialCaseUnitCodes { get; set; } = [];
    public List<string> PdcExcludedUnitCodes { get; set; } = [];
    /// <summary>
    /// Verified mapping of the original PACT report <c>Status</c> value to a payment status
    /// (Unpaid, PartiallyPaid or Paid). Empty until each value has been confirmed against PACT;
    /// any unmapped value is shown as Unknown with the original text retained.
    /// </summary>
    public Dictionary<string, string> SourceStatusMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
