using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Reconciliation;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>
/// The "PACT" side of a snapshot-consistency check: the procedure the snapshot refresh itself calls (<c>p4AccountReceivables</c> / <c>p32AccountReceivables</c>, NO suffix - see
/// <c>usp_CollectionsRcv_Refresh</c>), read over a direct connection (<c>PACTRPT</c>) instead of the linked server. Same procedure, same logic, different transport and time: it can
/// show that the refresh copied the procedure's output faithfully; it cannot show that the procedure is right. <c>CollectionsSource:PactReceivables:ProcedureSuffix</c> is deliberately
/// NOT used here, because the snapshot ignores it.
/// </summary>
public sealed class PactDirectReconciliationSource(PactSqlReceivablesSource pact) : IReconciliationSource
{
    /// <summary>The suffix of the procedure the refresh reads: none.</summary>
    public const string SnapshotProcedureSuffix = "";

    public string ProcedureLabel(int companyId) => PactSqlReceivablesSource.ProcedureName(companyId, SnapshotProcedureSuffix);

    public async Task<IReadOnlyList<PactReceivableInstalment>> ReadAsync(int companyId, DateOnly from, DateOnly through, CancellationToken cancellationToken) =>
        (await pact.ReadAsync(new PactReceivablesRequest(from, through, companyId), SnapshotProcedureSuffix, cancellationToken)).Items;
}

/// <summary>Runs a registered reconciliation on a background task of this process (own DI scope, not tied to the request).</summary>
public sealed class InProcessReconciliationLauncher(IServiceScopeFactory scopes, ILogger<InProcessReconciliationLauncher> logger) : IReconciliationLauncher
{
    public void Launch(Guid runId) => _ = Task.Run(async () =>
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<FinancialReconciliationService>().ExecuteAsync(runId, CancellationToken.None);
        }
        catch (Exception ex) { logger.LogError(ex, "Reconciliation run {RunId} failed outside its own error handling.", runId); }
    }, CancellationToken.None);
}
