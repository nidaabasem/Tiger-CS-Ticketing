using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.CustomerReviews.Abstractions;
using TigerCS.Domain.Modules.CustomerReviews;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.CustomerReviews;

public sealed class TicketReviewRepository(TigerCsDbContext dbContext) : ITicketReviewRepository
{
    public async Task AddInvitationAsync(TicketReviewInvitation invitation, CancellationToken cancellationToken = default) =>
        await dbContext.TicketReviewInvitations.AddAsync(invitation, cancellationToken);

    public Task<TicketReviewInvitation?> GetInvitationByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default) =>
        dbContext.TicketReviewInvitations.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);

    public Task<TicketReview?> GetReviewAsync(long ticketId, int reopenCycle, bool isTest, CancellationToken cancellationToken = default) =>
        dbContext.TicketReviews.AsNoTracking().FirstOrDefaultAsync(
            r => r.TicketId == ticketId && r.ReopenCycle == reopenCycle && r.IsTest == isTest, cancellationToken);

    public async Task<bool> TryAddReviewAsync(TicketReview review, CancellationToken cancellationToken = default)
    {
        dbContext.TicketReviews.Add(review);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            dbContext.Entry(review).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<IReadOnlyList<TicketReview>> ListReviewsForTicketAsync(long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketReviews.AsNoTracking()
            .Where(r => r.TicketId == ticketId)
            .OrderBy(r => r.ReopenCycle).ThenBy(r => r.IsTest)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TicketReviewInvitation>> ListInvitationsForTicketAsync(long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketReviewInvitations.AsNoTracking()
            .Where(i => i.TicketId == ticketId)
            .OrderBy(i => i.ReopenCycle).ThenBy(i => i.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<TicketReviewSummary> GetProductionSummaryAsync(CancellationToken cancellationToken = default)
    {
        var ratings = await dbContext.TicketReviews.AsNoTracking()
            .Where(r => !r.IsTest)
            .Select(r => (int)r.Rating)
            .ToListAsync(cancellationToken);

        return new TicketReviewSummary(ratings.Count, ratings.Count == 0 ? null : ratings.Average());
    }

    private static bool IsDuplicate(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: 2601 or 2627 })
            {
                return true;
            }
        }

        return false;
    }
}
