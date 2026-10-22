using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Reconciliation;

/// <summary>States of a reconciliation (a run, or one company within it). Missing evidence and real differences are different states on purpose.</summary>
public static class ReconciliationStates
{
    /// <summary>No reconciliation has run (or the application restarted since): nothing is known.</summary>
    public const string NotRun = "NotRun";
    public const string Running = "Running";
    /// <summary>The snapshot and the PACT procedure agree for every unit, within the tolerance.</summary>
    public const string Passed = "Passed";
    /// <summary>The comparison completed and found units that differ: the listed units say which and why.</summary>
    public const string Failed = "Failed";
    /// <summary>The comparison could not be completed (PACT unreachable, snapshot not fresh, snapshot republished meanwhile): NOT a finding about the data.</summary>
    public const string Error = "Error";
    /// <summary>A passing result older than <c>ReconciliationMaxAgeHours</c>.</summary>
    public const string Expired = "Expired";
}

public sealed record ReconciliationCompanyResultDto(
    int CompanyId, string CompanyName, string Status, string? Message, DateTime StartedUtc, DateTime FinishedUtc, DateTime? SnapshotSuccessUtc,
    string Procedure, ReconComparison? Comparison);

public sealed record ReconciliationRunDto(
    Guid RunId, string Status, DateTime StartedUtc, DateTime? FinishedUtc, DateOnly From, DateOnly Through, int? CompanyScope, decimal Tolerance, string? RequestedBy,
    IReadOnlyList<ReconciliationCompanyResultDto> Companies);

/// <summary>Whether the financial source may be used for approval and sending, and exactly why not when it may not.</summary>
public sealed record SourceGateDto(bool Reconciled, string Code, string Message, DateTime? EvidenceAtUtc, int MismatchUnits, bool SignOffRecorded, string? Reference);

public sealed record ReconciliationOverviewDto(SourceGateDto Gate, ReconciliationRunDto? Latest, IReadOnlyList<ReconciliationRunDto> Recent);

/// <summary>What the gate needs from the reconciliation side (kept small so Campaigns / Review depend on an abstraction, not on the service).</summary>
public interface IFinancialReconciliationEvidence
{
    /// <summary>The per-company evidence as of <paramref name="nowUtc"/>: one entry per company 4 and 32.</summary>
    IReadOnlyList<ReconciliationEvidenceItem> Evidence(DateTime nowUtc, int maxAgeHours);
}

public sealed record ReconciliationEvidenceItem(int CompanyId, string State, DateTime? AtUtc, int MismatchUnits, string? Message);

/// <summary>Launches a reconciliation run outside the request (background). Implemented in Infrastructure.</summary>
public interface IReconciliationLauncher
{
    void Launch(Guid runId);
}

/// <summary>Reads the same PACT procedure the snapshot is built from, directly and independently of the snapshot.</summary>
public interface IReconciliationSource
{
    string ProcedureLabel(int companyId);
    Task<IReadOnlyList<TigerCS.Application.Modules.Collections.Abstractions.PactReceivableInstalment>> ReadAsync(int companyId, DateOnly from, DateOnly through, CancellationToken cancellationToken);
}
