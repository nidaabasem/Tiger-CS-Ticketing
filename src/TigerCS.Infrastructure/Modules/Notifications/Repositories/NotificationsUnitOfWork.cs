using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Notifications.Repositories;

/// <summary>
/// The dispatcher's unit of work. Mirrors <c>TicketingUnitOfWork</c>'s shape,
/// with two dispatcher-specific operations that the other modules do not
/// need.
/// </summary>
public sealed class NotificationsUnitOfWork(TigerCsDbContext dbContext) : INotificationsUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// Persists the claim, reporting rather than throwing when it is lost.
    ///
    /// <para>
    /// <c>OutboxMessage.Attempts</c> is a concurrency token, so EF emits
    /// <c>UPDATE ... WHERE OutboxMessageId = @id AND Attempts = @original</c>.
    /// Two workers reading the same pending batch therefore race on one
    /// atomic conditional update, and exactly one wins. Losing is a routine
    /// outcome of running more than one worker — not an error — so it is a
    /// <c>false</c> here rather than an exception the caller must catch per
    /// message.
    /// </para>
    ///
    /// <para>
    /// The loser reloads the affected entries, which resets them to the
    /// database's values and marks them <c>Unchanged</c>. Without that the
    /// context would still hold a modified entity, and the <i>next</i>
    /// message's save would try to write it again.
    /// </para>
    /// </summary>
    public async Task<bool> TrySaveClaimAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                await entry.ReloadAsync(cancellationToken);
            }

            return false;
        }
    }

    public async Task DiscardPendingChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    // Reload rather than merely detach: a later query in the
                    // same pass could otherwise resolve the identity map to a
                    // stale, still-mutated instance.
                    await entry.ReloadAsync(cancellationToken);
                    break;
            }
        }
    }
}
