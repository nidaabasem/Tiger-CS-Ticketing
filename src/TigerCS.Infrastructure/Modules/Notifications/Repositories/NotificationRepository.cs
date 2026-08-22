using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Notifications.Repositories;

public sealed class NotificationRepository(TigerCsDbContext dbContext) : INotificationRepository
{
    public async Task AddAsync(Notification notification, CancellationToken cancellationToken = default) =>
        await dbContext.Notifications.AddAsync(notification, cancellationToken);

    /// <summary>
    /// Checks the change tracker before the database: within one dispatcher
    /// pass the row for this message may have been added moments ago and not
    /// yet committed, and a database-only lookup would miss it and add a
    /// second one.
    /// </summary>
    public async Task<Notification?> GetByOutboxMessageAsync(
        Guid outboxMessageId, NotificationType notificationType, CancellationToken cancellationToken = default)
    {
        var pending = dbContext.ChangeTracker.Entries<Notification>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .FirstOrDefault(n => n.OutboxMessageId == outboxMessageId && n.NotificationType == notificationType);

        if (pending is not null)
        {
            return pending;
        }

        return await dbContext.Notifications
            .FirstOrDefaultAsync(
                n => n.OutboxMessageId == outboxMessageId && n.NotificationType == notificationType,
                cancellationToken);
    }
}
