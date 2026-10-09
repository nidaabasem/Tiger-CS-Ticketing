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
    public void RefreshExtendsCoverageAndNeverShrinksItUnlessAskedTo_AndReadReportsALoadInProgress()
    {
        var refresh = Read("V005__usp_Collections_RefreshReceivables.sql");
        Assert.Contains("@ExtendCoverage     bit          = 1", refresh);
        Assert.Contains("CoverageFromDate    < @SourceFromDate    THEN CoverageFromDate", refresh);
        Assert.Contains("CoverageThroughDate > @SourceThroughDate THEN CoverageThroughDate", refresh);
        Assert.Contains("@CoverageFromDate = @cFrom, @CoverageThroughDate = @cThrough", refresh);   // the stored coverage is the window actually requested
        Assert.Contains("RefreshInProgress", Read("V006__usp_Collections_GetReceivables.sql"));
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
    public void ReadProceduresApplyWindowTowerMinimumAndClassificationInSql_AndNeverReadDirty()
    {
        var sql = Read("V006__usp_Collections_GetReceivables.sql");
        Assert.Contains("DATEADD(DAY, 1, CAST(@ToDate AS datetime))", sql);               // To is inclusive for the whole day
        Assert.Contains("s.DueDate < @ToExclusive", sql);
        Assert.Contains("s.TowerNumber = @TowerNumber", sql);                               // company is fixed by the run seek; the tower number is matched exactly
        Assert.Contains("s.Amount >= @MinAmount", sql);                                     // remaining unpaid amount >= minimum (inclusive)
        Assert.Contains("s.Amount >= @Min", sql);
        Assert.Contains("@ReceivableClass = 'DueOrOverdue' AND s.DueDate < @NextMonthStart", sql);
        Assert.Contains("CASE WHEN @PaymentFilter IN ('paid', 'all') THEN 0 ELSE @MinAmount END", sql);   // the minimum never hides a paid row
        Assert.Contains("st.CurrentRunId", sql);                                           // only the published run is visible
        Assert.Contains("OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY", sql);   // paging in SQL
        Assert.DoesNotContain("NOLOCK", sql, StringComparison.OrdinalIgnoreCase);          // dirty reads are not a performance fix
        Assert.DoesNotContain("READ UNCOMMITTED", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NoMatchingTower", sql);
    }

    [Fact]
    public void PublishNeverUsesDirtyReads_KeepsBatchesBelowLockEscalation_AndKeepsTheReplacedRun()
    {
        var sql = Read("V004__usp_Collections_PublishReceivablesStaging.sql");
        Assert.DoesNotContain("NOLOCK", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("READ UNCOMMITTED", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@batch int = 1500", sql);
        Assert.Contains("DELETE TOP (1500)", sql);
        Assert.Contains("RunId <> ISNULL(@oldRun", sql);                                   // cleanup never deletes the run a reader may still be on
        Assert.Contains("PaymentStatus", sql);
        Assert.Contains("ABS(PlanAmount - AllocatedAmount - Amount) <= 0.01", sql);        // original/paid amounts are stored only when they reconcile
    }

    [Fact]
    public void RefreshSupportsTheCompanionShapeAndRetainsPaidInstalmentsByDefault()
    {
        var sql = Read("V005__usp_Collections_RefreshReceivables.sql");
        Assert.Contains("@RetainPaid         bit          = 1", sql);
        Assert.Contains("@ProcedureSuffix    nvarchar(16) = N''", sql);                   // default: the deployed procedures, untouched
        Assert.Contains("PaymentTermAccountId, PlanAmount, AllocatedAmount", sql);
        Assert.Contains("@IncludeSettled = @IncludeSettled", sql);
        Assert.DoesNotContain("ALTER PROCEDURE [dbo].[p4", sql);                           // the existing procedures are never altered
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
