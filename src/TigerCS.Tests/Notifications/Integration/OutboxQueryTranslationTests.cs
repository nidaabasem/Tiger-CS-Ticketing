using Microsoft.EntityFrameworkCore;
using TigerCS.Infrastructure.Modules.Notifications.Repositories;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.Notifications.Integration;

/// <summary>
/// Proves the dispatcher's candidate query translates to T-SQL.
///
/// <para>
/// <b>Why this needs its own test.</b> Every other test in this increment
/// runs on the EF Core InMemory provider, which executes any expression
/// client-side and therefore cannot fail a translation. The eligibility
/// predicate is built as an expression tree — an OR-chain rebound onto one
/// parameter — so a translation failure would slip through the whole suite
/// and surface only in production, as a runtime exception on the one job
/// responsible for every customer-facing acknowledgement.
/// </para>
///
/// <para>
/// <c>ToQueryString()</c> compiles the query through the real SQL Server
/// provider without opening a connection, so this runs anywhere — no
/// database, no container.
/// </para>
/// </summary>
public class OutboxQueryTranslationTests
{
    private static TigerCsDbContext SqlServerContext() =>
        new(new DbContextOptionsBuilder<TigerCsDbContext>()
            .UseSqlServer("Server=(never-connected);Database=(never-connected);TrustServerCertificate=True;")
            .Options);

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void DispatchableQuery_TranslatesToSqlServer(int maxAttempts)
    {
        using var db = SqlServerContext();

        var sql = OutboxMessageRepository
            .Dispatchable(db, new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc), maxAttempts, TimeSpan.FromSeconds(30), 50)
            .ToQueryString();

        Assert.Contains("SELECT", sql, StringComparison.Ordinal);
        Assert.Contains("[OutboxMessages]", sql, StringComparison.Ordinal);

        // The whole predicate reached the database — no client-side
        // evaluation, no unbounded read during a backlog.
        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);

        // One OR term per possible attempt count.
        Assert.Equal(maxAttempts - 1, CountOccurrences(sql, " OR "));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
