using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Infrastructure.Modules.Collections;

namespace TigerCS.Tests.Collections.Reconciliation;

public sealed class DirectSourceProcedureTests
{
    [Theory]
    [InlineData("")]
    [InlineData("V2")]
    public void TheComparedProcedureIsAlwaysTheOneTheSnapshotRefreshCalls_WhateverSuffixIsConfigured(string configuredSuffix)
    {
        var options = new PactReceivablesOptions { ProcedureSuffix = configuredSuffix };
        var pact = new PactSqlReceivablesSource(new ConfigurationBuilder().Build(), options, TimeProvider.System, NullLogger<PactSqlReceivablesSource>.Instance);
        var source = new PactDirectReconciliationSource(pact);
        Assert.Equal("dbo.p4AccountReceivables", source.ProcedureLabel(4));
        Assert.Equal("dbo.p32AccountReceivables", source.ProcedureLabel(32));
        // ... and that is exactly the name usp_CollectionsRcv_Refresh builds (p<company>AccountReceivables, no suffix).
        var refresh = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "database", "collections-receivables", "R002__usp_CollectionsRcv_Refresh.sql"));
        Assert.Contains("QUOTENAME(CONCAT(N'p', @CompanyId, N'AccountReceivables'))", refresh);
    }
}
