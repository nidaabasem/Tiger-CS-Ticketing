using System.Data.SqlTypes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TigerCS.Tests.Collections.Sql;

/// <summary>
/// The allocation core in docs/Collections/pact-sql/allocation-core.sql is portable SQL (CTEs + window functions), so its
/// arithmetic is exercised on SQLite here. This verifies the algorithm and that the review procedures embed the exact tested
/// text; it does NOT execute T-SQL against PACT (no SQL Server in CI) - see the reconciliation scripts for that.
/// </summary>
public sealed class PactAllocationCoreTests : IDisposable
{
    private readonly SqliteConnection _db = new("Data Source=:memory:");

    public PactAllocationCoreTests()
    {
        _db.Open();
        Exec("CREATE TABLE Instalment (Tag TEXT, VoucherNo TEXT, AccountId INTEGER, DueDate TEXT, PlanAmount NUMERIC)");
        Exec("CREATE TABLE TagPaid (Tag TEXT, PaidAmount NUMERIC)");
    }

    public void Dispose() => _db.Dispose();

    private static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "docs", "Collections", "pact-sql", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(name);
    }

    private static string Squash(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    private void Exec(string sql) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    private void Plan(string tag, string voucher, int account, string due, decimal amount) =>
        Exec($"INSERT INTO Instalment VALUES ('{tag}','{voucher}',{account},'{due}',{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    private void Paid(string tag, decimal? amount) =>
        Exec($"INSERT INTO TagPaid VALUES ('{tag}',{(amount is null ? "NULL" : amount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))})");

    private List<(string Tag, string Voucher, decimal Plan, decimal Allocated)> RunCore()
    {
        var core = File.ReadAllText(RepoFile("allocation-core.sql"));
        using var c = _db.CreateCommand();
        c.CommandText = $"WITH {core} SELECT Tag, VoucherNo, PlanAmount, AllocatedAmount FROM Allocated ORDER BY Tag, DueDate, VoucherNo, AccountId";
        using var r = c.ExecuteReader();
        var rows = new List<(string, string, decimal, decimal)>();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3)));
        return rows;
    }

    /// <summary>The original @tabpaynew logic: cumulative plan of every row with DueDate &lt;= the row's date.</summary>
    private decimal OriginalRemainingTotal(string tag)
    {
        using var c = _db.CreateCommand();
        c.CommandText = """
            SELECT SUM(CASE WHEN PlanAmount - Alloc > 0 THEN PlanAmount - Alloc ELSE 0 END) FROM (
              SELECT PlanAmount, CASE WHEN Alloc < 0 THEN 0 ELSE Alloc END AS Alloc FROM (
                SELECT o.PlanAmount,
                       CASE WHEN a.PaidAmount >= (SELECT SUM(i.PlanAmount) FROM Instalment i WHERE i.Tag = o.Tag AND i.DueDate <= o.DueDate)
                                 THEN (SELECT SUM(i.PlanAmount) FROM Instalment i WHERE i.Tag = o.Tag AND i.DueDate <= o.DueDate)
                            WHEN a.PaidAmount < (SELECT SUM(i.PlanAmount) FROM Instalment i WHERE i.Tag = o.Tag AND i.DueDate <= o.DueDate)
                                 THEN o.PlanAmount - (SELECT SUM(i.PlanAmount) FROM Instalment i WHERE i.Tag = o.Tag AND i.DueDate <= o.DueDate) + a.PaidAmount
                            ELSE 0 END AS Alloc
                FROM Instalment o JOIN TagPaid a ON a.Tag = o.Tag WHERE o.Tag = $tag))
            """;
        c.Parameters.AddWithValue("$tag", tag);
        return Convert.ToDecimal(c.ExecuteScalar());
    }

    [Fact]
    public void SameDateInstalments_OriginalDoubleCounts_CoreAllocatesExactly()
    {
        // Issue 3: two separate same-date rows of 100, total paid 150. True remainder is 50.
        Plan("T1", "V1", 1, "2026-10-05", 100); Plan("T1", "V2", 1, "2026-10-05", 100); Paid("T1", 150);
        Assert.Equal(100m, OriginalRemainingTotal("T1"));          // 50 + 50: the demonstrated defect
        var rows = RunCore();
        Assert.Equal(50m, rows.Sum(r => r.Plan - r.Allocated));
        Assert.Equal([0m, 50m], rows.Select(r => r.Plan - r.Allocated));   // deterministic: VoucherNo order within the date
    }

    [Fact]
    public void DistinctDates_AreSettledOldestFirst()
    {
        Plan("T1", "V1", 1, "2026-08-01", 100); Plan("T1", "V1", 1, "2026-09-01", 100); Plan("T1", "V1", 1, "2026-10-01", 100); Paid("T1", 150);
        Assert.Equal([0m, 50m, 100m], RunCore().Select(r => r.Plan - r.Allocated));
        Assert.Equal(150m, OriginalRemainingTotal("T1")); // identical to the original when dates are distinct
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(-40, 300)]     // negative paid (over-billed ledger) allocates nothing
    [InlineData(300, 0)]
    [InlineData(999, 0)]       // overpayment never makes an instalment negative
    [InlineData(299.99, 0.01)]
    public void PaidAmountBoundaries(double paid, double remaining)
    {
        Plan("T1", "V1", 1, "2026-08-01", 100); Plan("T1", "V2", 1, "2026-09-01", 100); Plan("T1", "V3", 1, "2026-10-01", 100);
        Paid("T1", (decimal)paid);
        var rows = RunCore();
        Assert.Equal((decimal)remaining, Math.Round(rows.Sum(r => r.Plan - r.Allocated), 4)); // proc rounds to decimal(19,4)
        Assert.All(rows, r => Assert.InRange(r.Allocated, 0m, r.Plan));
    }

    [Fact]
    public void MissingPaidAmount_LeavesEverythingUnpaid_AsTheOriginalDid_AndTagsAreIsolated()
    {
        Plan("T1", "V1", 1, "2026-08-01", 100); Paid("T1", null);
        Plan("T2", "V9", 1, "2026-08-01", 70); Paid("T2", 70);
        var rows = RunCore();
        Assert.Equal(100m, rows.Single(r => r.Tag == "T1").Plan - rows.Single(r => r.Tag == "T1").Allocated);
        Assert.Equal(0m, rows.Single(r => r.Tag == "T2").Plan - rows.Single(r => r.Tag == "T2").Allocated);
    }

    [Fact]
    public void RemainingTotalAlwaysEqualsPlanMinusPaid_ForRandomisedLedgers_AndIsInsertOrderIndependent()
    {
        var rng = new Random(20261008);
        for (var round = 0; round < 40; round++)
        {
            Exec("DELETE FROM Instalment"); Exec("DELETE FROM TagPaid");
            var plans = Enumerable.Range(0, rng.Next(1, 9))
                .Select(i => (V: $"V{rng.Next(1, 4)}", A: rng.Next(1, 3), D: $"2026-{rng.Next(1, 4):00}-{rng.Next(1, 3):00}", P: (decimal)rng.Next(1, 50) * 10))
                // The procedure groups by (Tag, VoucherNo, AccountId, DueDate) before allocating: identities are unique.
                .GroupBy(p => (p.V, p.A, p.D)).Select(g => (g.Key.V, g.Key.A, g.Key.D, P: g.Sum(x => x.P))).ToList();
            var paid = (decimal)rng.Next(0, 3000) / 10;
            foreach (var p in plans.OrderBy(_ => rng.Next())) Plan("T", p.V, p.A, p.D, p.P);
            Paid("T", paid);
            var first = RunCore();
            Assert.Equal(Math.Max(plans.Sum(p => p.P) - paid, 0m), Math.Round(first.Sum(r => r.Plan - r.Allocated), 4));
            Exec("DELETE FROM Instalment");
            foreach (var p in plans.OrderBy(_ => rng.Next())) Plan("T", p.V, p.A, p.D, p.P);
            Assert.Equal(first, RunCore());   // same result regardless of physical row order
        }
    }

    [Fact]
    public void RepeatedBalanceAcrossAccountsAndInvoices_OriginalPaidAmountIsWrong_TagLevelFormulaIsRight()
    {
        // Issue 4: #tab has one row per (account x invoice); each row carries InvAmount_i - Balance_account.
        Exec("CREATE TABLE Inv (Tag TEXT, Voucher TEXT, InvAmount NUMERIC)");
        Exec("CREATE TABLE Bal (Tag TEXT, AccountId INTEGER, Balance NUMERIC)");
        Exec("INSERT INTO Inv VALUES ('T','I1',100),('T','I2',200)");
        Exec("INSERT INTO Bal VALUES ('T',1,150)");
        using var c = _db.CreateCommand();
        c.CommandText = """
            SELECT (SELECT SUM(i.InvAmount - b.Balance) FROM Inv i JOIN Bal b ON b.Tag = i.Tag) AS OriginalPaid,
                   (SELECT SUM(InvAmount) FROM Inv) - (SELECT SUM(Balance) FROM Bal) AS TagPaid
            """;
        using var r = c.ExecuteReader(); r.Read();
        Assert.Equal(0m, r.GetDecimal(0));      // original: (100-150)+(200-150)
        Assert.Equal(150m, r.GetDecimal(1));    // invoiced 300 - outstanding 150 = paid 150
    }

    [Theory]
    [InlineData("p4AccountReceivablesV2.review.sql")]
    [InlineData("p32AccountReceivablesV2.review.sql")]
    public void ReviewProceduresEmbedTheTestedCoreVerbatim_AndOnlyCreateNewObjects(string file)
    {
        var text = File.ReadAllText(RepoFile(file));
        Assert.Contains(Squash(File.ReadAllText(RepoFile("allocation-core.sql"))), Squash(text));
        Assert.DoesNotContain("ALTER PROCEDURE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP PROCEDURE", text, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"CREATE PROCEDURE \[dbo\]\.\[p(4|32)AccountReceivablesV2\]", text);
        Assert.DoesNotContain("@@ALLOCATION_CORE@@", text);
        foreach (var write in new[] { "INSERT INTO pact", "UPDATE pact", "DELETE FROM pact", "INSERT INTO PACT", "UPDATE PACT", "DELETE FROM PACT" })
            Assert.DoesNotContain(write, text);
    }

    [Theory]
    [InlineData("receivables-reconciliation.company4.read-only.sql")]
    [InlineData("receivables-reconciliation.company32.read-only.sql")]
    public void ReconciliationScriptsAreReadOnlyAgainstPact(string file)
    {
        var text = File.ReadAllText(RepoFile(file));
        // Only temp tables (#...) may be created/written; nothing against a PACT database or schema object.
        Assert.DoesNotMatch(@"(?i)\b(INSERT\s+INTO|UPDATE|DELETE\s+FROM|TRUNCATE\s+TABLE|DROP\s+TABLE|ALTER|MERGE)\s+(?!#)(?!\s)[A-Za-z\[]", text.Replace("IF OBJECT_ID('tempdb..", "").Replace("DROP TABLE #", "-"));
        Assert.DoesNotContain("CREATE PROCEDURE", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MinAmount = 0", text);
    }

    [Theory]
    [InlineData(2026, 10, 31, 23, 59, 59, 997, true)]
    [InlineData(2026, 10, 31, 23, 59, 59, 998, true)]   // rounds down to .997 in SQL datetime
    [InlineData(2026, 10, 31, 23, 59, 59, 999, false)]  // rounds up to 1 Nov 00:00:00.000
    [InlineData(2026, 11, 1, 0, 0, 0, 0, false)]
    [InlineData(2026, 10, 1, 0, 0, 0, 0, true)]
    [InlineData(2026, 9, 30, 23, 59, 59, 997, false)]   // before the month: OverDue, not Due, but still <= EndDate
    public void MonthEndBoundary_UsesSqlDatetimeRounding(int y, int mo, int d, int h, int mi, int s, int ms, bool inMonth)
    {
        var end = Infrastructure(new DateOnly(2026, 10, 31));
        var stored = new SqlDateTime(new DateTime(y, mo, d, h, mi, s, ms)).Value;
        var monthStart = new DateTime(2026, 10, 1);
        Assert.Equal(inMonth, stored >= monthStart && stored <= end);
        Assert.Equal(inMonth, DateOnly.FromDateTime(stored).Month == 10 && DateOnly.FromDateTime(stored).Year == 2026);
    }

    private static DateTime Infrastructure(DateOnly day) => TigerCS.Infrastructure.Modules.Collections.PactSqlReceivablesSource.EndOfDay(day);
}
