using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages.Admin;

/// <summary>
/// Shared plumbing of every Administration page: the signed-in viewer,
/// the one-shot status/error messages that survive a POST → redirect, and
/// the translation of an Api failure into management-readable wording. No
/// authorization decision lives here — the folder convention gates the
/// pages and the Api enforces every write.
/// </summary>
public abstract class AdminPageModel : PageModel
{
    [TempData] public string? StatusMessage { get; set; }

    [TempData] public string? ErrorMessage { get; set; }

    public CurrentUser? Viewer => CurrentUser.FromPrincipal(User);

    protected static string DescribeFailure(ApiOutcome outcome, string? detail, string fallback) =>
        outcome switch
        {
            ApiOutcome.ValidationError => detail ?? "The request was not valid.",
            ApiOutcome.Conflict => detail ?? "The change conflicts with the current configuration.",
            ApiOutcome.NotFound => "The record no longer exists.",
            ApiOutcome.Forbidden or ApiOutcome.Unauthorized => "You are not allowed to perform this action.",
            ApiOutcome.Unreachable or ApiOutcome.BadGateway => "Tiger Ticketing System could not reach the ticketing service.",
            _ => detail ?? fallback
        };

    protected string DescribeFailure<T>(ApiResult<T> result, string fallback) => DescribeFailure(result.Outcome, result.Detail, fallback);

    protected string DescribeFailure(ApiResult result, string fallback) => DescribeFailure(result.Outcome, result.Detail, fallback);
}
