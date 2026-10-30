using TigerCS.Application.Modules.CustomerReviews.Abstractions;
using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Application.Modules.CustomerReviews.Services;

/// <summary>
/// The read model behind Ticket Details' Customer Satisfaction section, built from the persisted reviews and invitations — no
/// second review store. Same visibility rule as the ticket itself, so a review is readable exactly when the ticket is.
/// A cycle is <c>Reviewed</c> only when a submitted review row exists; a cycle with a link issued but no submission is
/// <c>Pending</c> and carries no rating. Tokens never leave the repository.
/// </summary>
public sealed class TicketReviewQueryAppService(
    ITicketRepository ticketRepository,
    ITicketReviewRepository reviewRepository,
    TicketQueryAppService ticketQueryAppService)
{
    public const string Pending = "Pending";
    public const string Reviewed = "Reviewed";

    public async Task<TicketQueryResultDto<TicketReviewsDto>> GetForTicketAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketQueryResultDto<TicketReviewsDto>.Failure(TicketQueryOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewTicketAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return TicketQueryResultDto<TicketReviewsDto>.Failure(TicketQueryOutcome.Forbidden);
        }

        var reviews = await reviewRepository.ListReviewsForTicketAsync(ticketId, cancellationToken);
        var invitations = await reviewRepository.ListInvitationsForTicketAsync(ticketId, cancellationToken);

        var cycles = new List<TicketReviewCycleDto>();

        foreach (var review in reviews)
        {
            var requestedAt = invitations
                .Where(i => i.ReopenCycle == review.ReopenCycle && i.IsTest == review.IsTest)
                .Select(i => (DateTime?)i.CreatedAtUtc)
                .Min();
            cycles.Add(new TicketReviewCycleDto(
                review.ReopenCycle, Reviewed, review.IsTest, review.Rating, review.Comment, review.SubmittedAtUtc, requestedAt));
        }

        foreach (var group in invitations.GroupBy(i => (i.ReopenCycle, i.IsTest)))
        {
            if (cycles.Any(c => c.ReopenCycle == group.Key.ReopenCycle && c.IsTest == group.Key.IsTest))
            {
                continue;
            }

            cycles.Add(new TicketReviewCycleDto(
                group.Key.ReopenCycle, Pending, group.Key.IsTest, null, null, null, group.Min(i => i.CreatedAtUtc)));
        }

        var ordered = cycles.OrderBy(c => c.ReopenCycle).ThenBy(c => c.IsTest).ToList();
        return TicketQueryResultDto<TicketReviewsDto>.Success(new TicketReviewsDto(ticketId, ordered));
    }
}
