using Microsoft.Data.SqlClient;

namespace TigerCS.Tests.Collections.Rcv;

/// <summary>
/// The migration scripts (database/collections-receivables/migration) executed against the real local SQL Server: M01 backup, M02 cleanup (every refusal, the dry run, the real run and - with a
/// trigger that looks at the transaction's locks while the DELETE runs - that the locks are held until the delete), and M90 rollback.
/// </summary>
public sealed partial class RcvRealSqlTests
{
    private const string Tag = "t9";

    private static string MigrationScript(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "database", "collections-receivables", "migration"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "database", "collections-receivables", "migration", file));
    }

    private static async Task RunMigration(string file, bool execute = false, int maxAge = 180, bool overwrite = false)
    {
        var sql = MigrationScript(file).Replace("N'20261011'", $"N'{Tag}'")
            .Replace("DECLARE @Execute bit = 0;", $"DECLARE @Execute bit = {(execute ? 1 : 0)};")
            .Replace("DECLARE @MaxAgeMinutes int = 180;", $"DECLARE @MaxAgeMinutes int = {maxAge};")
            .Replace("DECLARE @OverwriteExisting bit = 0;", $"DECLARE @OverwriteExisting bit = {(overwrite ? 1 : 0)};");
        await using var connection = new SqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 }; await command.ExecuteNonQueryAsync();
    }

    private static readonly string[] LegacyBackedUp =
        ["CollectionsReceivableRun", "CollectionsReceivableRunCompany", "CollectionsReceivableCompanyState", "CollectionsReceivableSnapshot", "CollectionsReceivableStaging",
         "CollectionsReceivableTowerSummary", "CollectionsReceivableUnit", "CollectionsReceivableUnitText", "CollectionsContactNorm"];

    /// <summary>A healthy new pipeline, a small legacy data set and a verified backup of it under the test tag.</summary>
    private static async Task SeedMigration()
    {
        await Seed();
        await DropBackups();
        await Exec("DELETE FROM dbo.CollectionsReceivableRunCompany; DELETE FROM dbo.CollectionsReceivableRun; DELETE FROM dbo.CollectionsContactNorm; " +
                   "DECLARE @r uniqueidentifier = NEWID(); INSERT dbo.CollectionsReceivableRun (RunId, TriggerSource, StartedUtc, Status) VALUES (@r, N'Legacy', SYSUTCDATETIME(), 'Succeeded'); " +
                   "INSERT dbo.CollectionsReceivableRunCompany (RunId, CompanyId, StartedUtc, Status) VALUES (@r, 4, SYSUTCDATETIME(), 'Succeeded'), (@r, 32, SYSUTCDATETIME(), 'Succeeded'); " +
                   "INSERT dbo.CollectionsContactNorm (Kind, Raw) VALUES (1, N'0501234567'), (2, N'a@example.test');");
        await RunMigration("M01__backup_legacy_snapshot.sql");
    }

    private static async Task DropBackups()
    {
        foreach (var t in LegacyBackedUp) await Exec($"DROP TABLE IF EXISTS dbo.Bak_{t}_{Tag};");
    }

    private static async Task CleanupMigration()
    {
        await Exec("DROP TRIGGER IF EXISTS dbo.trg_M02_test; DROP TABLE IF EXISTS dbo.M02TestLocks;");
        await DropBackups();
        await Exec("DELETE FROM dbo.CollectionsReceivableRunCompany; DELETE FROM dbo.CollectionsReceivableRun; DELETE FROM dbo.CollectionsContactNorm; " +
                   "DELETE FROM dbo.CollectionsReceivableCompanyState; INSERT dbo.CollectionsReceivableCompanyState (CompanyId) VALUES (4), (32);");
    }

    private static async Task<int> LegacyRows() => await Scalar<int>("SELECT (SELECT COUNT(*) FROM dbo.CollectionsReceivableRun) + (SELECT COUNT(*) FROM dbo.CollectionsReceivableRunCompany) + (SELECT COUNT(*) FROM dbo.CollectionsContactNorm)");

    private static async Task<int> RefusalNumber(Func<Task> run)
    {
        var ex = await Assert.ThrowsAsync<SqlException>(run);
        return ex.Number;
    }

    /// <summary>Holds a session-owned application lock on a second connection until disposed.</summary>
    private static async Task<SqlConnection> HoldLock(string resource)
    {
        var connection = new SqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand("DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = @res, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @r;", connection);
        command.Parameters.AddWithValue("@res", resource);
        Assert.True((int)(await command.ExecuteScalarAsync())! >= 0);
        return connection;
    }

    [SqlFact]
    public async Task M02_Refuses_UntilEveryConditionHolds_AndNothingIsDeletedWhenItRefuses()
    {
        await SeedMigration();
        try
        {
            var before = await LegacyRows(); Assert.True(before > 0);
            // L1: an old-pipeline refresh is running (its lock is held by another session).
            await using (await HoldLock("Collections.ReceivablesRefresh")) Assert.Equal(50912, await RefusalNumber(() => RunMigration("M02__cleanup_legacy_snapshot.sql", execute: true)));
            // L2: a refresh of the NEW snapshot is running for a company.
            await using (await HoldLock("Collections.Rcv.32")) Assert.Equal(50915, await RefusalNumber(() => RunMigration("M02__cleanup_legacy_snapshot.sql", execute: true)));
            // C3: a published copy is older than the freshness limit.
            Assert.Equal(50920, await RefusalNumber(() => RunMigration("M02__cleanup_legacy_snapshot.sql", execute: true, maxAge: 0)));
            // C2: the newest attempt of a company failed (an older good copy is being served).
            await Exec("UPDATE dbo.CollectionsRcvCompany SET LastStatus = 'Failed' WHERE CompanyId = 32");
            Assert.Equal(50919, await RefusalNumber(() => RunMigration("M02__cleanup_legacy_snapshot.sql", execute: true)));
            await Exec("UPDATE dbo.CollectionsRcvCompany SET LastStatus = 'Running' WHERE CompanyId = 32");
            Assert.Equal(50919, await RefusalNumber(() => RunMigration("M02__cleanup_legacy_snapshot.sql", execute: true)));
            await Exec("UPDATE dbo.CollectionsRcvCompany SET LastStatus = 'Succeeded' WHERE CompanyId = 32");
            // C1: a copy without its rows / not a succeeded run / a company never published.
            await Exec("UPDATE dbo.CollectionsRcvRun SET Status = 'Failed' WHERE RunId = (SELECT CurrentRunId FROM dbo.CollectionsRcvCompany WHERE CompanyId = 4)");
            Assert.Equal(50917, await RefusalNumber(() => RunMigration("M02__cleanup_legacy_snapshot.sql", execute: true)));
            await Exec("UPDATE dbo.CollectionsRcvRun SET Status = 'Succeeded' WHERE RunId = (SELECT CurrentRunId FROM dbo.CollectionsRcvCompany WHERE CompanyId = 4)");
            await Exec("DELETE TOP (1) FROM dbo.CollectionsRcvLine WHERE CompanyId = 32");
            Assert.Equal(50918, await RefusalNumber(() => RunMigration("M02__cleanup_legacy_snapshot.sql", execute: true)));
            await Seed(); await RunMigrationBackupsAgain();
            await Exec("UPDATE dbo.CollectionsRcvCompany SET CurrentRunId = NULL WHERE CompanyId = 4");
            Assert.Equal(50911, await RefusalNumber(() => RunMigration("M02__cleanup_legacy_snapshot.sql", execute: true)));
            await Seed(); await RunMigrationBackupsAgain();
            // C4: a missing backup / a backup that no longer matches.
            await Exec($"DROP TABLE dbo.Bak_CollectionsContactNorm_{Tag}");
            Assert.Equal(50913, await RefusalNumber(() => RunMigration("M02__cleanup_legacy_snapshot.sql", execute: true)));
            await RunMigration("M01__backup_legacy_snapshot.sql");
            await Exec("INSERT dbo.CollectionsContactNorm (Kind, Raw) VALUES (1, N'0509999999')");        // the legacy table changed after the backup
            Assert.Equal(50914, await RefusalNumber(() => RunMigration("M02__cleanup_legacy_snapshot.sql", execute: true)));
            Assert.Equal(before + 1, await LegacyRows());                                                  // nothing was deleted by any refusal
        }
        finally { await CleanupMigration(); }
    }

    private static async Task RunMigrationBackupsAgain()
    {
        await DropBackups();
        await RunMigration("M01__backup_legacy_snapshot.sql");
    }

    [SqlFact]
    public async Task M02_DryRunDeletesNothingAndReleasesTheLocks_TheRealRunHoldsThemThroughTheDelete_AndRollbackRestores()
    {
        await SeedMigration();
        try
        {
            var before = await LegacyRows(); Assert.True(before > 0);
            await RunMigration("M02__cleanup_legacy_snapshot.sql", execute: false);
            Assert.Equal(before, await LegacyRows());                                                      // dry run: every check ran, nothing was deleted ...
            await using (var probe = await HoldLock("Collections.ReceivablesRefresh")) { }                  // ... and the locks are free again afterwards
            await using (var probe = await HoldLock("Collections.Rcv.4")) { }

            // While the DELETE of the real run executes, the transaction must STILL hold the old lock and both new company locks.
            await Exec("CREATE TABLE dbo.M02TestLocks (OldLock int, Dubai int, Sharjah int, OpenTransactions int);");
            await Exec("CREATE TRIGGER dbo.trg_M02_test ON dbo.CollectionsReceivableRun AFTER DELETE AS BEGIN SET NOCOUNT ON; " +
                       "INSERT dbo.M02TestLocks SELECT " +
                       "(SELECT COUNT(*) FROM sys.dm_tran_locks WHERE request_session_id = @@SPID AND resource_type = 'APPLICATION' AND resource_description LIKE '%Collections.ReceivablesRefresh%'), " +
                       "(SELECT COUNT(*) FROM sys.dm_tran_locks WHERE request_session_id = @@SPID AND resource_type = 'APPLICATION' AND resource_description LIKE '%Collections.Rcv.4%'), " +
                       "(SELECT COUNT(*) FROM sys.dm_tran_locks WHERE request_session_id = @@SPID AND resource_type = 'APPLICATION' AND resource_description LIKE '%Collections.Rcv.32%'), @@TRANCOUNT; END;");
            await RunMigration("M02__cleanup_legacy_snapshot.sql", execute: true);
            Assert.Equal(0, await LegacyRows());
            Assert.Equal("1/1/1", await Scalar<string>("SELECT TOP 1 CONCAT(OldLock, '/', Dubai, '/', Sharjah) FROM dbo.M02TestLocks WHERE OpenTransactions >= 1"));   // inside the open transaction the three locks are held
            Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsReceivableCompanyState WHERE CurrentRunId IS NULL"));   // re-seeded as "never loaded"
            Assert.Equal(before, await Scalar<int>($"SELECT (SELECT COUNT(*) FROM dbo.Bak_CollectionsReceivableRun_{Tag}) + (SELECT COUNT(*) FROM dbo.Bak_CollectionsReceivableRunCompany_{Tag}) + (SELECT COUNT(*) FROM dbo.Bak_CollectionsContactNorm_{Tag})"));
            Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvCompany WHERE CurrentRunId IS NOT NULL"));      // the new snapshot is untouched
            await using (var probe = await HoldLock("Collections.ReceivablesRefresh")) { }                  // released at the commit

            await Exec("DROP TRIGGER dbo.trg_M02_test;");
            await RunMigration("M90__rollback_restore_legacy.sql");                                          // rollback: the backups come back
            Assert.Equal(before, await LegacyRows());
        }
        finally { await CleanupMigration(); }
    }
}
