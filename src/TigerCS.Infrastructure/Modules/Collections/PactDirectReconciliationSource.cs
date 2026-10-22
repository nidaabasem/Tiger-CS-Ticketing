using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Reconciliation;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>The independent side of a reconciliation: the configured PACT report procedure read directly (the code path the snapshot job used before the local snapshot existed), never the snapshot.</summary>
public sealed class PactDirectReconciliationSource(PactSqlReceivablesSource pact, PactReceivablesOptions options) : IReconciliationSource
{
    public string ProcedureLabel(int companyId) => PactSqlReceivablesSource.ProcedureName(companyId, options.ProcedureSuffix);

    public async Task<IReadOnlyList<PactReceivableInstalment>> ReadAsync(int companyId, DateOnly from, DateOnly through, CancellationToken cancellationToken) =>
        (await pact.ReadAsync(new PactReceivablesRequest(from, through, companyId), cancellationToken)).Items;
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
