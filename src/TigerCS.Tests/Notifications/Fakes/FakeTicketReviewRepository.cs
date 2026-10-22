using TigerCS.Application.Modules.CustomerReviews.Abstractions;
using TigerCS.Domain.Modules.CustomerReviews;

namespace TigerCS.Tests.Notifications.Fakes;

/// <summary>
/// In-memory <see cref="ITicketReviewRepository"/> that enforces the same uniqueness the database does: a token hash identifies
/// one invitation, and one review exists per (ticket, cycle, test) — a second <see cref="TryAddReviewAsync"/> returns <c>false</c>.
/// </summary>
public sealed class FakeTicketReviewRepository : ITicketReviewRepository
{
    private long _nextInvitationId = 1;
    private long _nextReviewId = 1;

    public List<TicketReviewInvitation> Invitations { get; } = [];

    public List<TicketReview> Reviews { get; } = [];

    public Task AddInvitationAsync(TicketReviewInvitation invitation, CancellationToken cancellationToken = default)
    {
        typeof(TicketReviewInvitation).GetProperty(nameof(TicketReviewInvitation.TicketReviewInvitationId))!
            .SetValue(invitation, _nextInvitationId++);
        Invitations.Add(invitation);
        return Task.CompletedTask;
    }

    public Task<TicketReviewInvitation?> GetInvitationByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(Invitations.FirstOrDefault(i => i.TokenHash.SequenceEqual(tokenHash)));

    public Task<TicketReview?> GetReviewAsync(long ticketId, int reopenCycle, bool isTest, CancellationToken cancellationToken = default) =>
        Task.FromResult(Reviews.FirstOrDefault(r => r.TicketId == ticketId && r.ReopenCycle == reopenCycle && r.IsTest == isTest));

    public Task<bool> TryAddReviewAsync(TicketReview review, CancellationToken cancellationToken = default)
    {
        if (Reviews.Any(r => r.TicketId == review.TicketId && r.ReopenCycle == review.ReopenCycle && r.IsTest == review.IsTest))
        {
            return Task.FromResult(false);
        }

        typeof(TicketReview).GetProperty(nameof(TicketReview.TicketReviewId))!.SetValue(review, _nextReviewId++);
        Reviews.Add(review);
        return Task.FromResult(true);
    }

    public Task<TicketReviewSummary> GetProductionSummaryAsync(CancellationToken cancellationToken = default)
    {
        var production = Reviews.Where(r => !r.IsTest).ToList();
        return Task.FromResult(new TicketReviewSummary(production.Count, production.Count == 0 ? null : production.Average(r => (double)r.Rating)));
    }
}
