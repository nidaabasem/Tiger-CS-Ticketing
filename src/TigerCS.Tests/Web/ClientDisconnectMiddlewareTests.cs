using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Infrastructure.Http;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Web;

/// <summary>
/// The client-disconnect guard both hosts run: a request the BROWSER
/// abandoned is an expected cancellation, and a request that failed is still
/// a failure.
///
/// <para>
/// This is the server half of the double-submit story. An agent who clicks
/// Customers, or Apply on its filter bar, twice in quick succession makes the
/// browser reset the first connection; Kestrel cancels that request's
/// <see cref="HttpContext.RequestAborted"/>; the token the controller handed
/// down cancels the in-flight EF Core query; and
/// <c>CustomerDirectoryRepository.ListAsync</c> throws
/// <see cref="TaskCanceledException"/> out of its <c>CountAsync</c> or its
/// paged <c>ToListAsync</c>. Nothing there is broken — but before this
/// middleware the exception left the pipeline unhandled, so it was logged as
/// an application error and mapped to a 500 for a browser that had already
/// stopped listening.
/// </para>
///
/// <para>
/// The discrimination is what these tests pin down: an
/// <see cref="OperationCanceledException"/> WITH
/// <see cref="HttpContext.RequestAborted"/> actually cancelled is the client
/// leaving; everything else — a SQL command timeout, a
/// <see cref="TaskCanceledException"/> raised while the browser is still
/// waiting, any other fault — passes straight through to the exception
/// handler and its 500.
/// </para>
/// </summary>
public sealed class ClientDisconnectMiddlewareTests
{
    /// <summary>The real extension method over the real middleware, terminated by <paramref name="handler"/>.</summary>
    private static RequestDelegate Pipeline(CapturingLogger<ClientDisconnectMiddleware> log, RequestDelegate handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<ClientDisconnectMiddleware>>(log);
        var builder = new ApplicationBuilder(services.BuildServiceProvider());
        builder.UseTigerCsClientDisconnectHandling();
        builder.Run(handler);
        return builder.Build();
    }

    private static DefaultHttpContext Request(string method, string path, CancellationToken requestAborted)
    {
        var context = new DefaultHttpContext { RequestAborted = requestAborted };
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    // ---- the browser walked away ----

    /// <summary>
    /// The exact shape the Customers directory produces: SqlClient surfaces a
    /// cancelled command as <see cref="TaskCanceledException"/>, which is an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    [Fact]
    public async Task AbortedRequest_TaskCanceledException_IsAnswered499_AndNotLoggedAsAnError()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var log = new CapturingLogger<ClientDisconnectMiddleware>();
        var pipeline = Pipeline(log, _ => throw new TaskCanceledException("A task was canceled."));
        var context = Request("GET", "/api/customers", aborted.Token);

        await pipeline(context);

        Assert.Equal(ClientDisconnectMiddleware.ClientClosedRequest, context.Response.StatusCode);
        Assert.Equal(499, context.Response.StatusCode);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("/api/customers", entry.Message);
        Assert.Contains("client disconnected", entry.Message);
    }

    /// <summary>EF Core's own cancellation — raised from the token rather than by the provider — is the same event.</summary>
    [Fact]
    public async Task AbortedRequest_OperationCanceledException_IsAnswered499()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var log = new CapturingLogger<ClientDisconnectMiddleware>();
        var pipeline = Pipeline(log, _ => throw new OperationCanceledException(aborted.Token));

        var context = Request("GET", "/api/customers", aborted.Token);
        await pipeline(context);

        Assert.Equal(499, context.Response.StatusCode);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    /// <summary>A request that completed normally is untouched — the guard costs a try/catch and nothing else.</summary>
    [Fact]
    public async Task SuccessfulRequest_IsUntouched_AndLogsNothing()
    {
        var log = new CapturingLogger<ClientDisconnectMiddleware>();
        var pipeline = Pipeline(log, context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        var context = Request("GET", "/api/customers", CancellationToken.None);
        await pipeline(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Empty(log.Entries);
    }

    // ---- the browser is still waiting: every one of these is a real failure ----

    /// <summary>
    /// A SQL command timeout. SqlClient raises this as <c>SqlException</c>
    /// number -2 — not a cancellation of any kind — while the browser is
    /// still on the line, so it must reach the exception handler and become
    /// the logged error and 500 ProblemDetails it always was.
    /// </summary>
    [Fact]
    public async Task SqlTimeout_WhileTheClientIsStillWaiting_IsRethrown()
    {
        var log = new CapturingLogger<ClientDisconnectMiddleware>();
        var timeout = new TimeoutException("Execution Timeout Expired. The timeout period elapsed prior to completion of the operation.");
        var pipeline = Pipeline(log, _ => throw timeout);

        var context = Request("GET", "/api/customers", CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() => pipeline(context));
        Assert.Same(timeout, thrown);
        Assert.Empty(log.Entries);
    }

    /// <summary>
    /// The sharp case. A <see cref="TaskCanceledException"/> is NOT by itself
    /// a client disconnect — SqlClient can raise one for an async command
    /// that timed out, and an internal timeout of ours would too. With
    /// <see cref="HttpContext.RequestAborted"/> unsignalled the browser is
    /// still waiting for an answer, so this stays an error.
    /// </summary>
    [Fact]
    public async Task TaskCanceledException_WithoutAnAbortedRequest_IsRethrown()
    {
        var log = new CapturingLogger<ClientDisconnectMiddleware>();
        var pipeline = Pipeline(log, _ => throw new TaskCanceledException("A task was canceled."));

        var context = Request("GET", "/api/customers", CancellationToken.None);

        await Assert.ThrowsAsync<TaskCanceledException>(() => pipeline(context));
        Assert.Empty(log.Entries);
    }

    /// <summary>An ordinary defect is never mistaken for a departure, even on a connection the client has since dropped.</summary>
    [Fact]
    public async Task OtherException_OnAnAbortedRequest_IsStillRethrown()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var log = new CapturingLogger<ClientDisconnectMiddleware>();
        var pipeline = Pipeline(log, _ => throw new InvalidOperationException("boom"));

        var context = Request("GET", "/api/customers", aborted.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline(context));
        Assert.Empty(log.Entries);
    }
}
