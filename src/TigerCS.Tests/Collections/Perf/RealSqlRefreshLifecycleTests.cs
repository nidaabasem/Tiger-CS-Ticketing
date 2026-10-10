using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Infrastructure.Modules.Collections;

namespace TigerCS.Tests.Collections.Perf;

/// <summary>
/// Run lifecycle against a REAL SQL Server holding the deployed snapshot objects and the local fake PACT (database/collections-receivables/tests/local-sqlserver,
/// loopback linked server [10.10.10.94]). They run only when TIGERCS_PERF_SQL holds a connection string to the throw-away TigerCsTicketing database (never in
/// CI); otherwise they return immediately. They prove that a refresh that is cancelled or times out while the PACT fetch is slow leaves NO run / company in
/// 'Running' and records the failure on the company. They cannot reproduce the production linked server (provider, MSDTC) or real PACT volumes.
/// </summary>
public sealed class RealSqlRefreshLifecycleTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TIGERCS_PERF_SQL");

    private static async Task<T> Scalar<T>(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task Exec(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }

    private static SqlReceivablesRefresher Refresher(int timeoutSeconds = 1800)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TigerCsDatabase"] = ConnectionString }).Build();
        return new SqlReceivablesRefresher(config, new ReceivablesSnapshotOptions { RefreshCommandTimeoutSeconds = timeoutSeconds }, NullLogger<SqlReceivablesRefresher>.Instance);
    }

    private static async Task<(int Running, string State, int? Error)> Observe()
    {
        var running = await Scalar<int>("SELECT (SELECT COUNT(*) FROM dbo.CollectionsReceivableRun WHERE Status = 'Running') + (SELECT COUNT(*) FROM dbo.CollectionsReceivableRunCompany WHERE Status = 'Running')");
        var state = await Scalar<string>("SELECT LastAttemptStatus FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = 32");
        var error = await Scalar<object>("SELECT ISNULL(CAST(LastErrorNumber AS int), -999999) FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = 32");
        return (running, state, (int)error == -999999 ? null : (int)error);
    }

    private static async Task WaitForLockRelease()
    {
        for (var i = 0; i < 60; i++)
        {
            if (await Scalar<int>("DECLARE @r int; EXEC @r = sys.sp_getapplock N'Collections.ReceivablesRefresh', N'Exclusive', N'Session', 0; IF @r >= 0 EXEC sys.sp_releaseapplock N'Collections.ReceivablesRefresh', N'Session'; SELECT @r") >= 0) return;
            await Task.Delay(1000);
        }
    }

    [Fact]
    public async Task CancelledWhileSharjahIsSlow_LeavesNothingRunning()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Exec("UPDATE PACTRPT.dbo.FakeConfig SET DelaySeconds = 30 WHERE CompanyId = 32");
        try
        {
            // Measured: a cancellation does not interrupt a batch blocked in the linked-server call, so the call may finish normally or throw.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            try { await Refresher().RefreshAsync("lifecycle-cancel", 32, cts.Token); } catch (Exception ex) when (ex is OperationCanceledException or SqlException) { }
            await WaitForLockRelease();
            var seen = await Observe();
            Assert.Equal(0, seen.Running);
        }
        finally { await Exec("UPDATE PACTRPT.dbo.FakeConfig SET DelaySeconds = 0 WHERE CompanyId = 32"); await WaitForLockRelease(); }
    }

    [Fact]
    public async Task CommandTimeoutWhileSharjahIsSlow_LeavesNothingRunning_AndTheNextRefreshWorks()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Exec("UPDATE PACTRPT.dbo.FakeConfig SET DelaySeconds = 90 WHERE CompanyId = 32");
        try
        {
            var result = await Refresher(timeoutSeconds: 30).RefreshAsync("lifecycle-timeout", 32, CancellationToken.None);
            Assert.Equal("Failed", result.Status);
            var seen = await Observe();
            Assert.Equal(0, seen.Running);
            Assert.Equal("Failed", seen.State);
        }
        finally { await Exec("UPDATE PACTRPT.dbo.FakeConfig SET DelaySeconds = 0 WHERE CompanyId = 32"); await WaitForLockRelease(); }
        var again = await Refresher().RefreshAsync("lifecycle-retry", 32, CancellationToken.None);   // Retry is not blocked by what was left behind
        Assert.Equal("Succeeded", again.Status);
        Assert.Equal(0, (await Observe()).Running);
    }
}
