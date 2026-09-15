using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TigerCS.Infrastructure.Http;

/// <summary>
/// Treats a request the CLIENT walked away from as the ordinary event it is,
/// not as an application failure.
///
/// <para>
/// When a browser abandons a request — the agent clicks Apply on the
/// Customers filter twice, hits Enter again, or navigates away while a page
/// is still loading — the browser resets the connection. Kestrel signals
/// that by cancelling <see cref="HttpContext.RequestAborted"/>, which is the
/// token every handler, application service and EF Core query in this
/// solution already flows. The in-flight query therefore throws an
/// <see cref="OperationCanceledException"/> (SqlClient surfaces it as a
/// <see cref="TaskCanceledException"/>, which is one) from wherever it was
/// waiting — for the Customers directory, from the <c>CountAsync</c> or the
/// paged <c>ToListAsync</c> inside <c>CustomerDirectoryRepository.ListAsync</c>.
/// That is cancellation working exactly as designed: the abandoned work stops
/// instead of running on for a reader who has already gone.
/// </para>
///
/// <para>
/// Without this middleware that exception leaves the pipeline unhandled, so
/// the host logs it at Error alongside genuine defects and the exception
/// handler maps it to a 500 — a 500 written to a socket nobody is reading.
/// This catches exactly that case, logs it at Debug, and answers
/// <see cref="ClientClosedRequest">499 Client Closed Request</see>.
/// </para>
///
/// <para>
/// The discriminator is deliberately narrow: <see cref="HttpContext.RequestAborted"/>
/// must actually be cancelled. A SQL command timeout is not a client
/// disconnect — SqlClient raises <c>SqlException</c> number -2 while the
/// browser is still waiting, <see cref="HttpContext.RequestAborted"/> is
/// unsignalled, and nothing here touches it: it stays an unhandled
/// exception, logged as an error and returned as a 500 ProblemDetails. The
/// same holds for an <see cref="OperationCanceledException"/> raised by any
/// timeout of our own — that is a failure, not a departure, so it is
/// rethrown.
/// </para>
/// </summary>
public sealed class ClientDisconnectMiddleware(RequestDelegate next, ILogger<ClientDisconnectMiddleware> logger)
{
    /// <summary>499 Client Closed Request — nginx's de facto code for "the client hung up", and what ASP.NET Core's own exception handler uses for an aborted request.</summary>
    public const int ClientClosedRequest = 499;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Expected, and routine on a list page an agent can re-submit
            // faster than the database can answer. Debug, never Error: it
            // says nothing about the health of this application, and at
            // Warning or above it would drown the failures that do.
            logger.LogDebug(
                "{HttpMethod} {Path} was cancelled because the client disconnected before the response was produced.",
                context.Request.Method, context.Request.Path);

            // A response already on the wire cannot be relabelled, and there
            // is no longer anyone to read it either way.
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ClientClosedRequest;
            }
        }
    }
}

/// <summary>Registers <see cref="ClientDisconnectMiddleware"/>.</summary>
public static class ClientDisconnectMiddlewareExtensions
{
    /// <summary>
    /// Adds the client-disconnect guard. Register it INSIDE the exception
    /// handler (that is, immediately after <c>UseExceptionHandler</c>) so an
    /// abandoned request is recognised here before the generic handler logs
    /// it as an unhandled exception; anything that is not a client
    /// disconnect passes straight through to that handler untouched.
    /// </summary>
    public static IApplicationBuilder UseTigerCsClientDisconnectHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<ClientDisconnectMiddleware>();
}
