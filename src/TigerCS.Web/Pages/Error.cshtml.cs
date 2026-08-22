using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TigerCS.Web.Pages;

/// <summary>
/// The catch-all page for an unhandled exception and for any status code the
/// pipeline re-executes here.
///
/// <para>
/// Nothing about the underlying failure reaches the browser: no exception
/// type, no message, no stack trace, no path. The correlation id is shown so a
/// user can quote it to support and an operator can find the matching log
/// line, which is the whole of what a user needs.
/// </para>
/// </summary>
[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ErrorModel(ILogger<ErrorModel> logger) : PageModel
{
    public int HttpStatus { get; private set; } = 500;

    public string Title { get; private set; } = "Something went wrong";

    public string Message { get; private set; } =
        "The page could not be shown. Please try again, or contact support if it keeps happening.";

    public string CorrelationId { get; private set; } = string.Empty;

    public void OnGet(int? status = null)
    {
        CorrelationId = HttpContext.TraceIdentifier;
        HttpStatus = status ?? 500;

        (Title, Message) = HttpStatus switch
        {
            400 => ("That request wasn't valid",
                "The information sent with the request could not be understood."),
            401 => ("Please sign in",
                "Your session has ended. Sign in again to continue."),
            403 => ("You don't have access",
                "Your account does not have permission for this. If you think it should, ask your supervisor."),
            404 => ("Page not found",
                "That address doesn't match anything in the Service Desk."),
            502 or 503 or 504 => ("Service unavailable",
                "The ticketing service could not be reached. Please try again shortly."),
            _ => (Title, Message)
        };

        var exception = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        if (exception is not null)
        {
            logger.LogError(
                exception.Error,
                "Unhandled exception on {Path}. CorrelationId={CorrelationId}",
                exception.Path,
                CorrelationId);
        }
    }
}
