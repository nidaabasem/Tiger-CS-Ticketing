using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Notifications.Repositories;

public sealed class OutboxMessageRepository(TigerCsDbContext dbContext) : IOutboxMessageRepository
{
    public async Task<IReadOnlyList<OutboxMessage>> GetDispatchableAsync(
        DateTime nowUtc, int maxAttempts, TimeSpan baseRetryDelay, int batchSize, CancellationToken cancellationToken = default)
    {
        // Backoff is a derived schedule, not a stored NextAttemptAtUtc column
        // (MVP-Data-Dictionary.md §2.23 has none, and this increment does not
        // invent one). Rather than pull every pending row and filter in
        // memory, the eligibility rule is expanded into one OR-clause per
        // possible attempt count and pushed to the database: Attempts is
        // bounded by maxAttempts, so this is a handful of terms, and the
        // cutoff for each is a constant computed here.
        //
        // Doing it this way keeps the whole predicate SQL-translatable, which
        // matters because the alternative — fetching all pending rows to
        // filter client-side — would read an unbounded set during a backlog,
        // exactly when the dispatcher is under most pressure.
        var eligibleAttemptCounts = new List<(int Attempts, DateTime LatestOccurredAtUtc)>();
        for (var attempts = 0; attempts < maxAttempts; attempts++)
        {
            // A message with this many completed attempts is eligible once
            // now >= OccurredAtUtc + cumulativeBackoff(attempts), i.e. once
            // OccurredAtUtc <= now - cumulativeBackoff(attempts).
            var offset = OutboxMessage.NextEligibleAtUtc(DateTime.UnixEpoch, attempts, baseRetryDelay) - DateTime.UnixEpoch;
            eligibleAttemptCounts.Add((attempts, nowUtc - offset));
        }

        var query = dbContext.OutboxMessages.Where(m => m.Status == OutboxMessageStatus.Pending);

        var predicate = eligibleAttemptCounts
            .Select(BuildAttemptPredicate)
            .Aggregate(OrElse);

        return await query
            .Where(predicate)
            .OrderBy(m => m.OccurredAtUtc)
            .ThenBy(m => m.OutboxMessageId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<OutboxMessage?> GetByIdAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) =>
        await dbContext.OutboxMessages.FirstOrDefaultAsync(m => m.OutboxMessageId == outboxMessageId, cancellationToken);

    private static System.Linq.Expressions.Expression<Func<OutboxMessage, bool>> BuildAttemptPredicate(
        (int Attempts, DateTime LatestOccurredAtUtc) window) =>
        m => m.Attempts == window.Attempts && m.OccurredAtUtc <= window.LatestOccurredAtUtc;

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
