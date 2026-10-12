namespace TigerCS.Tests.Collections.Rcv;

/// <summary>
/// A test that needs a REAL SQL Server (the throw-away TigerCsTicketing database named by TIGERCS_PERF_SQL, see database/collections-receivables/tests/local-sqlserver).
/// Without that variable the test is reported as SKIPPED - it never "passes" without having executed any SQL.
/// </summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public const string Variable = "TIGERCS_PERF_SQL";
    public SqlFactAttribute(string? alsoRequires = null)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable))) Skip = SkipReason;
        else if (alsoRequires is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(alsoRequires))) Skip = $"Not executed: {alsoRequires} is not set.";
    }
    public static string SkipReason => $"Not executed: {Variable} is not set (real SQL Server required).";
}

/// <summary>The <see cref="SqlFactAttribute"/> counterpart for data-driven tests.</summary>
public sealed class SqlTheoryAttribute : TheoryAttribute
{
    public SqlTheoryAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlFactAttribute.Variable))) Skip = SqlFactAttribute.SkipReason; }
}
