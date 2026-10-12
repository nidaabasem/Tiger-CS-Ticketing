using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Infrastructure.Modules.Collections;

namespace TigerCS.Tests.Collections.Rcv;

/// <summary>V013 on a real SQL Server (synthetic project ids 9xx, removed afterwards): evidence is stored once per observation, a cancelled (starred) PACT code is never stored, and the map procedure only returns promoted rows.</summary>
public sealed class CrmProjectEvidenceRealSqlTests
{
    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlFactAttribute.Variable)!;

    [SqlFact]
    public async Task Evidence_IsRecordedOncePerObservation_NeverFromAStarredCode_AndNeverBecomesAMapping()
    {
        var options = new ReceivablesSnapshotOptions();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"ConnectionStrings:{options.ConnectionStringName}"] = ConnectionString }).Build();
        var store = new SqlCollectionsCrmProjectEvidenceStore(config, options);
        var map = new SqlCollectionsCrmProjectMap(config, options);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        async Task Exec(string sql) { await using var c = new SqlCommand(sql, connection); await c.ExecuteNonQueryAsync(); }
        async Task<int> Count(string sql) { await using var c = new SqlCommand(sql, connection); return (int)(await c.ExecuteScalarAsync())!; }
        await Exec("DELETE FROM dbo.CollectionsCrmProjectMapEvidence WHERE CrmProjectId BETWEEN 900 AND 909");
        try
        {
            var good = new CrmProjectEvidence(900, "TP125", 4, 3302, 1, "3302", "T1", "TP125-3302");
            await store.RecordAsync([good, good with { CrmUnitId = 3303, PactUnitCode = "TP125-3303" }, good with { CrmProjectId = 901, PactUnitCode = "TP125-3302*" }, good with { CrmProjectId = 902, PactUnitCode = "TP125-**3302" }], default);
            await store.RecordAsync([good], default);

            Assert.Equal(2, await Count("SELECT COUNT(*) FROM dbo.CollectionsCrmProjectMapEvidence WHERE CrmProjectId = 900"));
            Assert.Equal(2, await Count("SELECT ObservedCount FROM dbo.CollectionsCrmProjectMapEvidence WHERE CrmProjectId = 900 AND CrmUnitId = 1"));
            Assert.Equal(0, await Count("SELECT COUNT(*) FROM dbo.CollectionsCrmProjectMapEvidence WHERE CrmProjectId IN (901, 902)"));
            Assert.Empty((await map.GetAsync([900, 901, 902], default)).SelectMany(x => x.Value));      // evidence alone is never a mapping
        }
        finally { await Exec("DELETE FROM dbo.CollectionsCrmProjectMapEvidence WHERE CrmProjectId BETWEEN 900 AND 909"); }
    }
}
