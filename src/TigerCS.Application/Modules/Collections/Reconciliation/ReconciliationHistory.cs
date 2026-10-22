namespace TigerCS.Application.Modules.Collections.Reconciliation;

/// <summary>
/// The reconciliation runs of this process, newest first (a registered singleton). Deliberately in memory: after a restart there is no evidence and the gate stays closed until a
/// run is made again - the safe direction. It is also the single-flight / cooldown guard, so neither page views nor repeated clicks can start a second PACT read while one runs.
/// </summary>
public sealed class ReconciliationHistory : IFinancialReconciliationEvidence
{
    private const int Keep = 20;
    private readonly object _gate = new();
    private readonly List<ReconciliationRunDto> _runs = [];
    private DateTime _lastStartUtc = DateTime.MinValue;

    /// <summary>Registers a new running run, unless one is already running or the last one started less than <paramref name="cooldown"/> ago.</summary>
    public bool TryBegin(ReconciliationRunDto run, TimeSpan cooldown, out string? refusal)
    {
        lock (_gate)
        {
            if (_runs.Any(r => r.Status == ReconciliationStates.Running)) { refusal = "A reconciliation is already running."; return false; }
            if (run.StartedUtc - _lastStartUtc < cooldown) { refusal = $"A reconciliation was started less than {cooldown.TotalMinutes:0} minutes ago; wait for its result."; return false; }
            _lastStartUtc = run.StartedUtc;
            _runs.Insert(0, run);
            if (_runs.Count > Keep) _runs.RemoveRange(Keep, _runs.Count - Keep);
            refusal = null;
            return true;
        }
    }

    public void Replace(ReconciliationRunDto run)
    {
        lock (_gate)
        {
            var i = _runs.FindIndex(r => r.RunId == run.RunId);
            if (i >= 0) _runs[i] = run;
        }
    }

    public ReconciliationRunDto? Get(Guid runId) { lock (_gate) return _runs.FirstOrDefault(r => r.RunId == runId); }
    public ReconciliationRunDto? Latest() { lock (_gate) return _runs.FirstOrDefault(); }
    public IReadOnlyList<ReconciliationRunDto> Recent() { lock (_gate) return [.. _runs]; }

    /// <summary>
    /// Per company, the newest CONCLUSIVE result (Passed or Failed) decides; an Error (PACT unreachable, snapshot not fresh) never revokes earlier evidence but is reported when nothing
    /// conclusive exists. A pass older than the maximum age is Expired.
    /// </summary>
    public IReadOnlyList<ReconciliationEvidenceItem> Evidence(DateTime nowUtc, int maxAgeHours)
    {
        lock (_gate)
        {
            var items = new List<ReconciliationEvidenceItem>();
            foreach (var company in new[] { 4, 32 })
            {
                var results = _runs.Where(r => r.Status != ReconciliationStates.Running)
                    .SelectMany(r => r.Companies.Where(c => c.CompanyId == company)).OrderByDescending(c => c.FinishedUtc).ToList();
                var conclusive = results.FirstOrDefault(c => c.Status is ReconciliationStates.Passed or ReconciliationStates.Failed);
                if (conclusive is not null)
                {
                    var state = conclusive.Status == ReconciliationStates.Passed && nowUtc - conclusive.FinishedUtc > TimeSpan.FromHours(maxAgeHours) ? ReconciliationStates.Expired : conclusive.Status;
                    items.Add(new(company, state, conclusive.FinishedUtc, conclusive.Comparison?.MismatchedUnits ?? 0, conclusive.Message));
                }
                else if (results.FirstOrDefault() is { } error) items.Add(new(company, ReconciliationStates.Error, error.FinishedUtc, 0, error.Message));
                else items.Add(new(company, ReconciliationStates.NotRun, null, 0, null));
            }
            return items;
        }
    }
}
