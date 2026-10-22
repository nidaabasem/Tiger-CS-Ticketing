namespace TigerCS.Application.Modules.CustomerReviews.Dto;

public enum ReviewLinkStatus
{
    /// <summary>Unknown, malformed or tampered token. Deliberately indistinguishable from each other.</summary>
    Invalid = 0,

    /// <summary>The link is genuine and a review may be submitted.</summary>
    Valid = 1,

    /// <summary>The link is genuine but past its expiry.</summary>
    Expired = 2,

    /// <summary>A review was already submitted for this ticket; it is preserved and cannot be changed.</summary>
    AlreadySubmitted = 3
}

/// <summary>
/// What the public review page may know about a link. The ticket number is the only ticket detail — it is already in the
/// email the customer received — and it is released only for a valid link.
/// </summary>
public sealed record ReviewLinkDto(ReviewLinkStatus Status, string? TicketNumber = null);

public sealed record SubmitReviewRequestDto(int Rating, string? Comment);

public enum SubmitReviewOutcome
{
    Submitted = 0,
    Invalid = 1,
    Expired = 2,
    AlreadySubmitted = 3,
    InvalidRating = 4,
    InvalidComment = 5
}

public sealed record SubmitReviewResultDto(SubmitReviewOutcome Outcome, string? TicketNumber = null);

/// <summary>The links placed in a Resolved/Closed email. Never contain anything but the opaque token (and the rating).</summary>
/// <param name="PageUrl">The review page without a pre-selected rating.</param>
/// <param name="StarUrls">Five links, index 0 = 1 star … index 4 = 5 stars. Opening one only pre-selects the rating; nothing is saved.</param>
/// <param name="ExpiresAtUtc">When the links stop working.</param>
public sealed record ReviewInvitationLinks(string PageUrl, IReadOnlyList<string> StarUrls, DateTime ExpiresAtUtc);

/// <summary>The review token from the email link, sent in a request body so it never appears in a URL or access log.</summary>
public sealed record ReviewTokenRequestDto(string? Token);

/// <summary>The token, the star rating and the optional comment.</summary>
public sealed record SubmitTicketReviewRequestDto(string? Token, int Rating, string? Comment);

/// <summary>
/// One review cycle of a ticket as shown in Ticket Details. Carries no token, token hash or link — only what the customer
/// submitted and when. <paramref name="Rating"/>, <paramref name="Comment"/> and <paramref name="SubmittedAtUtc"/> are
/// <c>null</c> until the customer has actually submitted.
/// </summary>
/// <param name="ReopenCycle">0 for the original resolution; 1, 2 … after each reopen.</param>
/// <param name="Status"><c>Pending</c> (a review was requested, none submitted) or <c>Reviewed</c>.</param>
/// <param name="IsTest">Submitted through a Test-mode link; excluded from production statistics.</param>
/// <param name="Rating">The submitted 1–5 rating; null while Pending.</param>
/// <param name="Comment">The customer's optional comment; null when none was given.</param>
/// <param name="SubmittedAtUtc">When the customer submitted, in UTC; null while Pending.</param>
/// <param name="RequestedAtUtc">When the review was (first) requested by email, in UTC.</param>
public sealed record TicketReviewCycleDto(
    int ReopenCycle,
    string Status,
    bool IsTest,
    int? Rating,
    string? Comment,
    DateTime? SubmittedAtUtc,
    DateTime? RequestedAtUtc);

/// <summary>The customer satisfaction reviews of one ticket, oldest cycle first.</summary>
public sealed record TicketReviewsDto(long TicketId, IReadOnlyList<TicketReviewCycleDto> Cycles);
