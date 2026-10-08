namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>
/// Static checks on the versioned deployment scripts (no SQL Server is available to CI). They pin the rules the application and
/// the documentation depend on; the executable T-SQL smoke test is database/collections-receivables/tests.
/// </summary>
public sealed class SqlScriptContractTests
{
    private static string Dir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "database", "collections-receivables"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("database/collections-receivables not found"), "database", "collections-receivables");
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(Dir(), file));

    [Fact]
    public void ScriptsAreVersionedInOrder() =>
        Assert.Equal(["V001__reconcile_CollectionsTowers.sql", "V002__create_receivables_snapshot_tables.sql", "V003__fn_CollectionsTowerNumber.sql",
            "V004__usp_Collections_PublishReceivablesStaging.sql", "V005__usp_Collections_RefreshReceivables.sql", "V006__usp_Collections_GetReceivables.sql"],
            Directory.GetFiles(Dir(), "V*.sql").Select(f => Path.GetFileName(f)!).Order().ToArray());

    [Fact]
    public void TowerTableScriptIsNonDestructive()
    {
        var sql = Read("V001__reconcile_CollectionsTowers.sql");
        foreach (var forbidden in new[] { "DROP TABLE", "TRUNCATE", "DELETE FROM dbo.CollectionsTowers", "DBCC CHECKIDENT", "SET IDENTITY_INSERT", "ALTER TABLE", "ALTER COLUMN", "INSERT dbo.CollectionsTowers", "INSERT INTO dbo.CollectionsTowers" })
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IF OBJECT_ID(N'dbo.CollectionsTowers', N'U') IS NULL", sql);
        Assert.DoesNotContain("119", sql.Replace("Tower 119 is intentionally NOT seeded", ""));   // no invented tower
    }

    [Fact]
    public void RefreshUsesLinkedServerProceduresWithoutAHardCodedThreshold_AndPreventsOverlap()
    {
        var sql = Read("V005__usp_Collections_RefreshReceivables.sql");
        Assert.Contains("N'10.10.10.94'", sql);
        Assert.Contains("N'PACTRPT'", sql);
        Assert.Contains("AccountReceivables", sql);
        Assert.Contains("sp_getapplock", sql);
        Assert.Contains("REMOTE_PROC_TRANSACTIONS OFF", sql);
        Assert.Contains("@SourceMinAmount    int          = 0", sql);                      // default 0, never the diagnostic 100
        Assert.DoesNotContain("@MinAmount = 100", sql);
        Assert.DoesNotContain("'20260101'", sql);                                        // the accounting window is not truncated to 1 January
        Assert.Contains("'20000101'", sql);
        Assert.Contains("PartialFailure", sql);
    }

    [Fact]
    public void PublishValidatesBeforePublishingAndKeepsThePreviousSnapshotOnFailure()
    {
        var sql = Read("V004__usp_Collections_PublishReceivablesStaging.sql");
        foreach (var guard in new[] { "THROW 50011", "THROW 50012", "THROW 50013", "THROW 50014", "THROW 50015", "THROW 50016" })
            Assert.Contains(guard, sql);
        Assert.Contains("Amount > 0 AND UnitID > 0", sql);                                  // paid and UnitID 0 never published
        Assert.Contains("UPDATE dbo.CollectionsReceivableCompanyState", sql);
        Assert.Contains("CurrentRunId = @RunId", sql);                                       // publication is a pointer flip
        // The failure path must not move the pointer or the last-success time.
        var failure = sql[sql.IndexOf("usp_Collections_RecordReceivablesFailure", StringComparison.Ordinal)..];
        var recordBody = failure[..failure.IndexOf("GO", StringComparison.Ordinal)];
        Assert.DoesNotContain("CurrentRunId =", recordBody);
        Assert.DoesNotContain("LastSuccessUtc =", recordBody);
    }

    [Fact]
    public void ReadProcedureAppliesWindowTowerAndClassificationInSql()
    {
        var sql = Read("V006__usp_Collections_GetReceivables.sql");
        Assert.Contains("DATEADD(DAY, 1, CAST(@ToDate AS datetime))", sql);               // To is inclusive for the whole day
        Assert.Contains("s.DueDate < @ToExclusive", sql);
        Assert.Contains("s.CompanyId = @TowerCompany AND s.TowerNumber = @TowerNumber", sql);   // company AND tower number
        Assert.Contains("@ReceivableClass = 'DueOrOverdue' AND s.DueDate < @NextMonthStart", sql);
        Assert.DoesNotContain("ProjectCode =", sql.Replace("s.ProjectCode,", ""));          // ProjectCode is never the mapping source
        Assert.Contains("st.CurrentRunId = s.RunId", sql);                                   // only the published run is visible
        Assert.Contains("NoMatchingTower", sql);
    }

    [Fact]
    public void TowerFunctionImplementsTheDocumentedRule()
    {
        var sql = Read("V003__fn_CollectionsTowerNumber.sql");
        Assert.Contains("UPPER(LEFT(@s, 2)) = N'TP'", sql);
        Assert.Contains("CHARINDEX(N'-', @s)", sql);
    }

    [Fact]
    public void NoScriptContainsCredentials()
    {
        foreach (var file in Directory.GetFiles(Dir(), "*.sql", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Password=", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PWD=", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("User ID=crmpact", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
