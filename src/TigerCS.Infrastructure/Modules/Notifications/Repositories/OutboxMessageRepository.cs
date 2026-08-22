using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Notifications.Repositories;

public sealed class OutboxMessageRepository(TigerCsDbContext dbContext) : IOutboxMessageRepository
{
    public async Task<IReadOnlyList<OutboxMessage>> GetDispatchableAsync(
        DateTime nowUtc, int maxAttempts, TimeSpan baseRetryDelay, int batchSize, CancellationToken cancellationToken = default) =>
        await Dispatchable(dbContext, nowUtc, maxAttempts, baseRetryDelay, batchSize).ToListAsync(cancellationToken);

    /// <summary>
    /// The dispatcher's candidate query, as a composed <see cref="IQueryable{T}"/>.
    ///
    /// <para>
    /// Exposed separately from <see cref="GetDispatchableAsync"/> so a test
    /// can render it with <c>ToQueryString()</c> against a SQL Server-
    /// configured context and prove it translates. That check matters here
    /// specifically: the OR-chain below is built as an expression tree, and a
    /// translation failure would surface only against a real SQL Server —
    /// never against the EF Core InMemory provider the integration tests run
    /// on, which executes any expression client-side.
    /// </para>
    /// </summary>
    public static IQueryable<OutboxMessage> Dispatchable(
        TigerCsDbContext dbContext, DateTime nowUtc, int maxAttempts, TimeSpan baseRetryDelay, int batchSize)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        // Backoff is a derived schedule, not a stored NextAttemptAtUtc column
        // (MVP-Data-Dictionary.md §2.23 has none, and this increment does not
        // invent one). Rather than pull every pending row and filter in
        // memory, the eligibility rule is expanded into one OR-clause per
        // possible attempt count and pushed to the database: Attempts is
        // bounded by maxAttempts, so this is a handful of terms, and the
        // cutoff for each is a constant computed here.
        //
        // Filtering client-side instead would read an unbounded set during a
        // backlog — exactly when the dispatcher is under the most pressure.
        var perAttemptPredicates = new List<System.Linq.Expressions.Expression<Func<OutboxMessage, bool>>>();

        for (var attempts = 0; attempts < maxAttempts; attempts++)
        {
            // Captured as two plain scalar locals rather than as a tuple: a
            // closed-over scalar is the parameterisation pattern EF handles
            // as a matter of course.
            var attemptCount = attempts;

            // A message with this many completed attempts is eligible once
            // now >= OccurredAtUtc + cumulativeBackoff(attempts), i.e. once
            // OccurredAtUtc <= now - cumulativeBackoff(attempts).
            var offset = OutboxMessage.NextEligibleAtUtc(DateTime.UnixEpoch, attemptCount, baseRetryDelay) - DateTime.UnixEpoch;
            var latestOccurredAtUtc = nowUtc - offset;

            perAttemptPredicates.Add(m => m.Attempts == attemptCount && m.OccurredAtUtc <= latestOccurredAtUtc);
        }

        return dbContext.OutboxMessages
            .Where(m => m.Status == OutboxMessageStatus.Pending)
            .Where(perAttemptPredicates.Aggregate(OrElse))
            .OrderBy(m => m.OccurredAtUtc)
            .ThenBy(m => m.OutboxMessageId)
            .Take(batchSize);
    }

    public async Task<OutboxMessage?> GetByIdAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) =>
        await dbContext.OutboxMessages.FirstOrDefaultAsync(m => m.OutboxMessageId == outboxMessageId, cancellationToken);

    private static System.Linq.Expressions.Expression<Func<OutboxMessage, bool>> OrElse(
        System.Linq.Expressions.Expression<Func<OutboxMessage, bool>> left,
        System.Linq.Expressions.Expression<Func<OutboxMessage, bool>> right)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(typeof(OutboxMessage), "m");
        var body = System.Linq.Expressions.Expression.OrElse(
            new ParameterRebinder(parameter).Visit(left.Body)!,
            new ParameterRebinder(parameter).Visit(right.Body)!);

        return System.Linq.Expressions.Expression.Lambda<Func<OutboxMessage, bool>>(body, parameter);
    }

    /// <summary>Rewrites two separately-built lambdas onto one shared parameter so they can be OR-ed into a single translatable expression tree.</summary>
    private sealed class ParameterRebinder(System.Linq.Expressions.ParameterExpression parameter)
        : System.Linq.Expressions.ExpressionVisitor
    {
        protected override System.Linq.Expressions.Expression VisitParameter(System.Linq.Expressions.ParameterExpression node) =>
            node.Type == parameter.Type ? parameter : base.VisitParameter(node);
    }
}
