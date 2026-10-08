namespace TigerCS.Application.Modules.Collections;

/// <summary>Direct PACT report reads; independent of the per-customer EDSM APIs.</summary>
public sealed class PactReceivablesOptions
{
    public const string SectionName = "CollectionsSource:PactReceivables";
    public bool Enabled { get; set; }
    public string ConnectionStringName { get; set; } = "PACTRPT";
    public int CommandTimeoutSeconds { get; set; } = 60;
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
}
