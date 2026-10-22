namespace TigerCS.Domain.Modules.CustomerReviews;

/// <summary>
/// The customer's satisfaction review of a ticket: a 1–5 star rating and an optional comment. At most one exists per ticket
/// cycle and environment kind (unique index on <c>TicketId, ReopenCycle, IsTest</c>), so a second submission — a double click,
/// a replayed request, a second email — can never overwrite or duplicate it.
/// </summary>
public class TicketReview
{
    public const int MinRating = 1;
    public const int MaxRating = 5;
    public const int CommentMaxLength = 1000;

    public long TicketReviewId { get; private set; }

    public long TicketId { get; private set; }

    public int ReopenCycle { get; private set; }

    public long TicketReviewInvitationId { get; private set; }

    /// <summary>
    /// True when submitted through a Test-mode link. Production satisfaction reporting always filters on <c>IsTest = false</c>,
    /// so UAT reviews cannot contaminate customer statistics.
    /// </summary>
    public bool IsTest { get; private set; }

    public byte Rating { get; private set; }

    public string? Comment { get; private set; }

    public DateTime SubmittedAtUtc { get; private set; }

    private TicketReview()
    {
    }

    public TicketReview(TicketReviewInvitation invitation, int rating, string? comment, DateTime submittedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        if (rating is < MinRating or > MaxRating)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), "A rating must be between 1 and 5.");
        }

        TicketId = invitation.TicketId;
        ReopenCycle = invitation.ReopenCycle;
        TicketReviewInvitationId = invitation.TicketReviewInvitationId;
        IsTest = invitation.IsTest;
        Rating = (byte)rating;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        SubmittedAtUtc = submittedAtUtc;
    }
}
