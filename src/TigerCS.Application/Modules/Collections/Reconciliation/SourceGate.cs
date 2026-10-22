namespace TigerCS.Application.Modules.Collections.Reconciliation;

/// <summary>
/// THE definition of "the financial source is reconciled", used by the Campaigns preview, the stored review data and the approval summary alike (they used to disagree:
/// one looked at the sign-off flag only, the other also at the procedure). Fail-closed: every branch that is not a full pass explains itself.
/// </summary>
public static class SourceGate
{
    public const string Reconciled = "Reconciled", SignOffMissing = "SignOffMissing", ProcedureChanged = "ProcedureChanged",
        EvidenceNotRun = "EvidenceNotRun", EvidenceFailed = "EvidenceFailed", EvidenceError = "EvidenceError", EvidenceExpired = "EvidenceExpired";

    public static SourceGateDto Evaluate(CollectionsCampaignOptions campaign, PactReceivablesOptions source, IFinancialReconciliationEvidence? evidence, DateTime nowUtc)
    {
        var result = EvaluateCore(campaign, source, evidence, nowUtc);
        // The snapshot refresh always reads p4/p32AccountReceivables; ProcedureSuffix only concerns the archived direct reader. A sign-off recorded "for V2" would not cover the data in use.
        var warning = string.IsNullOrEmpty(source.ProcedureSuffix) ? null
            : $"ProcedureSuffix \"{source.ProcedureSuffix}\" is configured, but the receivables snapshot is always refreshed from p4/p32AccountReceivables (no suffix). A sign-off must cover the procedure that is actually read.";
        return result with { LedgerVerificationAttested = result.SignOffRecorded, Warning = warning };
    }

    private static SourceGateDto EvaluateCore(CollectionsCampaignOptions campaign, PactReceivablesOptions source, IFinancialReconciliationEvidence? evidence, DateTime nowUtc)
    {
        var reference = string.IsNullOrWhiteSpace(campaign.ReconciliationReference) ? null : campaign.ReconciliationReference.Trim();
        var signed = campaign.FinancialSourceValidated;
        if (!signed)
            return new(false, SignOffMissing, "No reconciliation sign-off is recorded for the configured PACT procedure (Collections:Campaigns:FinancialSourceValidated is off). A person must review the reconciliation and record the sign-off.", null, 0, false, reference);
        if (!string.Equals(campaign.ValidatedProcedureSuffix ?? "", source.ProcedureSuffix ?? "", StringComparison.Ordinal))
            return new(false, ProcedureChanged, $"The sign-off was recorded for procedure suffix \"{campaign.ValidatedProcedureSuffix}\" but \"{source.ProcedureSuffix}\" is configured now; it must be reconciled again.", null, 0, true, reference);
        if (!campaign.RequireAutomatedReconciliation || evidence is null)
            return new(true, Reconciled, "Reconciled by sign-off only (the automated snapshot-consistency check is not required by configuration).", null, 0, true, reference);

        var items = evidence.Evidence(nowUtc, Math.Max(1, campaign.ReconciliationMaxAgeHours));
        string[] companies = ["Tiger Group Dubai", "Tiger Group Sharjah"];
        string Name(int id) => id == 4 ? companies[0] : companies[1];
        var failed = items.Where(i => i.State == ReconciliationStates.Failed).ToList();
        if (failed.Count > 0)
            return new(false, EvidenceFailed, $"The snapshot-consistency check found differences between the snapshot and the PACT procedure for {string.Join(" and ", failed.Select(f => Name(f.CompanyId)))} ({failed.Sum(f => f.MismatchUnits):N0} unit(s)); see the diagnostics.", failed.Max(f => f.AtUtc), failed.Sum(f => f.MismatchUnits), true, reference);
        var error = items.FirstOrDefault(i => i.State == ReconciliationStates.Error);
        if (error is not null)
            return new(false, EvidenceError, $"The latest snapshot-consistency check of {Name(error.CompanyId)} could not be completed ({error.Message}). That is not a finding about the data; run it again.", error.AtUtc, 0, true, reference);
        var missing = items.Where(i => i.State == ReconciliationStates.NotRun).ToList();
        if (missing.Count > 0)
            return new(false, EvidenceNotRun, $"No snapshot-consistency check has been run for {string.Join(" and ", missing.Select(m => Name(m.CompanyId)))}. Run it (it compares the snapshot with a fresh read of the same PACT procedure).", null, 0, true, reference);
        var expired = items.Where(i => i.State == ReconciliationStates.Expired).ToList();
        if (expired.Count > 0)
            return new(false, EvidenceExpired, $"The last passing snapshot-consistency check of {string.Join(" and ", expired.Select(m => Name(m.CompanyId)))} is older than {campaign.ReconciliationMaxAgeHours} hours; run it again.", expired.Min(e => e.AtUtc), 0, true, reference);
        return new(true, Reconciled, "Reconciled: a person has signed off the procedure against PACT, and the snapshot is consistent with a fresh read of that procedure for both companies. (The automated check alone is not an independent ledger verification.)", items.Min(i => i.AtUtc), 0, true, reference);
    }
}
