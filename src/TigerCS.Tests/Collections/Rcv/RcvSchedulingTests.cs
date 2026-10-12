using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Collections;
using TigerCS.Infrastructure.BackgroundJobs;

namespace TigerCS.Tests.Collections.Rcv;

/// <summary>The two-hourly refresh schedule: one recurring job per company, Asia/Dubai, the old job removed, nothing registered while Hangfire is off.</summary>
public sealed class RcvSchedulingTests
{
    private sealed class RecordingManager : IRecurringJobManager
    {
        public Dictionary<string, (Job Job, string Cron, TimeZoneInfo Zone)> Added { get; } = [];
        public List<string> Triggered { get; } = [];
        public List<string> Removed { get; } = [];
        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) => Added[recurringJobId] = (job, cronExpression, options.TimeZone);
        public void Trigger(string recurringJobId) => Triggered.Add(recurringJobId);
        public void RemoveIfExists(string recurringJobId) => Removed.Add(recurringJobId);
    }

    private static (RecordingManager Manager, IServiceProvider Services) Build()
    {
        var manager = new RecordingManager();
        return (manager, new ServiceCollection().AddSingleton<IRecurringJobManager>(manager).BuildServiceProvider());
    }

    [Fact]
    public void EachCompanyGetsItsOwnTwoHourlyJob_InDubai_AndTheOldJobIsRemoved()
    {
        var (manager, services) = Build();
        services.UseTigerCsRecurringCollectionsReceivablesRefresh(new BackgroundJobOptions { Enabled = true }, new ReceivablesSnapshotOptions());
        Assert.Equal(["collections-receivables-refresh-32", "collections-receivables-refresh-4"], manager.Added.Keys.Order());
        Assert.All(manager.Added.Values, j => { Assert.Equal("0 */2 * * *", j.Cron); Assert.Equal("Asia/Dubai", j.Zone.Id); });
        Assert.Equal([4, 32], manager.Added.OrderBy(a => a.Key.Length).Select(a => (int)a.Value.Job.Args[0]!));     // the company id is the job argument
        Assert.Contains("collections-receivables-refresh", manager.Removed);                                       // the previous pipeline's single job no longer runs
        Assert.Equal(["collections-receivables-refresh-32", "collections-receivables-refresh-4"], manager.Triggered.Order());   // RefreshOnStartup: one run per company right away
    }

    [Fact]
    public void WithoutHangfire_NothingIsRegistered_AndWithRefreshDisabledTheJobsAreRemoved()
    {
        var (off, offServices) = Build();
        offServices.UseTigerCsRecurringCollectionsReceivablesRefresh(new BackgroundJobOptions { Enabled = false }, new ReceivablesSnapshotOptions());
        Assert.Empty(off.Added); Assert.Empty(off.Removed); Assert.Empty(off.Triggered);

        var (manager, services) = Build();
        services.UseTigerCsRecurringCollectionsReceivablesRefresh(new BackgroundJobOptions { Enabled = true }, new ReceivablesSnapshotOptions { RefreshEnabled = false });
        Assert.Empty(manager.Added); Assert.Empty(manager.Triggered);
        Assert.Equal(["collections-receivables-refresh", "collections-receivables-refresh-32", "collections-receivables-refresh-4"], manager.Removed.Order());
    }

    /// <summary>Real Hangfire storage on the local SQL Server (TIGERCS_PERF_SQL): what Hangfire stored, and when it will fire next.</summary>
    [SqlFact]
    public async Task HangfireStoresTheCronTheZoneAndANextRunOnAnEvenDubaiHour()
    {
        var connectionString = Environment.GetEnvironmentVariable("TIGERCS_PERF_SQL")!;
        var storage = new SqlServerStorage(connectionString, new SqlServerStorageOptions { SchemaName = "HangfireRcvScheduleTest", PrepareSchemaIfNecessary = true, DisableGlobalLocks = true });
        var manager = new RecurringJobManager(storage);
        manager.AddOrUpdate("collections-receivables-refresh", () => Console.WriteLine("legacy"), "*/30 * * * *");        // what the previous release registered
        var services = new ServiceCollection().AddSingleton<IRecurringJobManager>(manager).BuildServiceProvider();
        services.UseTigerCsRecurringCollectionsReceivablesRefresh(new BackgroundJobOptions { Enabled = true }, new ReceivablesSnapshotOptions { RefreshOnStartup = false });

        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync();
        async Task<Dictionary<string, string>> Hash(string id)
        {
            var result = new Dictionary<string, string>();
            await using var command = new SqlCommand("SELECT Field, Value FROM HangfireRcvScheduleTest.Hash WHERE [Key] = @k", connection);
            command.Parameters.AddWithValue("@k", "recurring-job:" + id);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result[reader.GetString(0)] = reader.GetString(1);
            return result;
        }
        Assert.Empty(await Hash("collections-receivables-refresh"));                                        // the old job is gone
        foreach (var company in new[] { 4, 32 })
        {
            var job = await Hash($"collections-receivables-refresh-{company}");
            Assert.Equal("0 */2 * * *", job["Cron"]);
            Assert.Equal("Asia/Dubai", job["TimeZoneId"]);
            var next = DateTime.Parse(job["NextExecution"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            var dubai = next.AddHours(4);                                                                    // Gulf Standard Time, no daylight saving
            Assert.Equal((0, 0), (dubai.Minute, dubai.Second));
            Assert.Equal(0, dubai.Hour % 2);                                                                // 00:00, 02:00 ... 22:00 Dubai time
            var stored = InvocationData.DeserializePayload(job["Job"]).DeserializeJob();                          // what the Hangfire server will execute
            Assert.Equal(("CollectionsReceivablesRefreshJob", "RunAsync", company), (stored.Type.Name, stored.Method.Name, (int)stored.Args[0]!));
        }
        await Exec(connectionString, "DROP TABLE IF EXISTS HangfireRcvScheduleTest.[Hash], HangfireRcvScheduleTest.[Set], HangfireRcvScheduleTest.[List], HangfireRcvScheduleTest.[Counter], HangfireRcvScheduleTest.[JobParameter], HangfireRcvScheduleTest.[JobQueue], HangfireRcvScheduleTest.[State], HangfireRcvScheduleTest.[Job], HangfireRcvScheduleTest.[Server], HangfireRcvScheduleTest.[AggregatedCounter], HangfireRcvScheduleTest.[Schema], HangfireRcvScheduleTest.[Lock]; DROP SCHEMA IF EXISTS HangfireRcvScheduleTest;");
    }

    private static async Task Exec(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }
}
