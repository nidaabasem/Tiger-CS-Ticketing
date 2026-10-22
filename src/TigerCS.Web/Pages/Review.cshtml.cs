using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages;

public enum ReviewPageState
{
    Form,
    Thanks,
    Invalid,
    Expired,
    AlreadySubmitted,
    Unavailable
}

/// <summary>
/// The customer satisfaction review page — the target of the star links in Resolved and Closed emails
/// (<c>/Review?t=&lt;token&gt;&amp;r=&lt;1-5&gt;</c>). Anonymous: the customer has no TigerCS login; the opaque token is the
/// credential and TigerCS.Api validates it on every call.
///
/// <para>
/// <b>A GET never saves anything.</b> Email security scanners and link previews open email links; opening a star link here only
/// shows the form with that rating pre-selected. The review is committed by the explicit POST ("Submit review"), which also
/// carries ASP.NET Core's antiforgery token.
/// </para>
/// </summary>
public sealed class ReviewModel(ReviewsApiClient reviewsApiClient) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "t")]
    public string? Token { get; set; }

    /// <summary>The pre-selected / submitted star rating, 1–5; 0 when none was chosen.</summary>
    [BindProperty(SupportsGet = true, Name = "r")]
    public int Rating { get; set; }

    [BindProperty]
    public string? Comment { get; set; }

    public ReviewPageState State { get; private set; } = ReviewPageState.Invalid;

    public string? TicketNumber { get; private set; }

    public string? FormError { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        ApplyPrivacyHeaders();
        Rating = Rating is >= 1 and <= 5 ? Rating : 0;

        var lookup = await reviewsApiClient.LookupAsync(Token, cancellationToken);
        Apply(lookup);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        ApplyPrivacyHeaders();

        if (Rating is < 1 or > 5)
        {
            // Show the form again with a prompt, provided the link is still good.
            Rating = 0;
            Apply(await reviewsApiClient.LookupAsync(Token, cancellationToken));
            if (State == ReviewPageState.Form)
            {
                FormError = "Please choose a star rating before submitting.";
            }

            return Page();
        }

        var result = await reviewsApiClient.SubmitAsync(Token, Rating, Comment, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            State = ReviewPageState.Unavailable;
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Page();
        }

        TicketNumber = result.Value.TicketNumber;
        switch (result.Value.Outcome)
        {
            case SubmitReviewOutcome.Submitted:
                State = ReviewPageState.Thanks;
                break;
            case SubmitReviewOutcome.AlreadySubmitted:
                State = ReviewPageState.AlreadySubmitted;
                break;
            case SubmitReviewOutcome.Expired:
                State = ReviewPageState.Expired;
                break;
            case SubmitReviewOutcome.InvalidRating:
                State = ReviewPageState.Form;
                FormError = "Please choose a star rating between 1 and 5.";
                break;
            case SubmitReviewOutcome.InvalidComment:
                State = ReviewPageState.Form;
                FormError = "Your comment is too long. Please shorten it to 1,000 characters.";
                break;
            default:
                State = ReviewPageState.Invalid;
                break;
        }

        if (State == ReviewPageState.Form)
        {
            Apply(await reviewsApiClient.LookupAsync(Token, cancellationToken), keepFormError: true);
        }

        return Page();
    }

    private void Apply(ApiResult<ReviewLinkDto> lookup, bool keepFormError = false)
    {
        if (!lookup.IsSuccess || lookup.Value is null)
        {
            State = ReviewPageState.Unavailable;
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        TicketNumber = lookup.Value.TicketNumber;
        State = lookup.Value.Status switch
        {
            ReviewLinkStatus.Valid => ReviewPageState.Form,
            ReviewLinkStatus.Expired => ReviewPageState.Expired,
            ReviewLinkStatus.AlreadySubmitted => ReviewPageState.AlreadySubmitted,
            _ => ReviewPageState.Invalid
        };

        if (State != ReviewPageState.Form && !keepFormError)
        {
            FormError = null;
        }

        if (State is ReviewPageState.Invalid or ReviewPageState.Expired)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    /// <summary>The token is in the URL: never cache the response and never leak the URL onward in a Referer header.</summary>
    private void ApplyPrivacyHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }
}
