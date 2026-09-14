using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// A browser that starts a second navigation before the first has answered
/// hangs up on the first. Kestrel signals that as <c>RequestAborted</c>, the
/// token reaches EF Core, and the in-flight query throws — in the Customers
/// directory's case from inside CustomerDirectoryRepository.
///
/// That is an expected end to a request nobody is waiting for, not a fault:
/// it must not be logged as an application error and must not answer 500.
/// What must still be an error is a cancellation the client did NOT cause —
/// a command timeout, say — because nothing about that is expected.
///
/// The distinction the Api relies on is <c>RequestAborted</c>, not the token
/// carried by the exception: EF Core and SqlClient raise their own linked
/// tokens rather than rethrowing the request's, so the exception's token says
/// nothing about who hung up. These tests pin both halves.
/// </summary>
public sealed class ClientCancellationTests
{
    private sealed class CapturingProvider : ILoggerProvider
    {
        public readonly List<(LogLevel Level, string Category, string Message)> Entries = [];
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }

        public IEnumerable<string> AtOrAbove(LogLevel level) =>
            Entries.Where(e => e.Level >= level).Select(e => $"{e.Level} {e.Category}: {e.Message}");

        private sealed class Logger(CapturingProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries) { owner.Entries.Add((level, category, formatter(state, ex))); }
            }
        }
    }

    /// <summary>The Api's own pipeline — <c>AddProblemDetails()</c> plus <c>UseExceptionHandler()</c>, as Program.cs wires it.</summary>
    private static async Task<(HttpStatusCode? Status, CapturingProvider Log)> RunApiPipelineAsync(
        RequestDelegate handler, bool clientHangsUp, TaskCompletionSource reached)
    {
        var log = new CapturingProvider();
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureLogging(l => { l.ClearProviders(); l.AddProvider(log); l.SetMinimumLevel(LogLevel.Trace); });
            web.ConfigureServices(services => services.AddProblemDetails());
            web.Configure(app =>
            {
                app.UseExceptionHandler();
                app.Run(handler);
            });
        });

        using var host = await builder.StartAsync();
        using var cts = new CancellationTokenSource();
        var call = host.GetTestClient().GetAsync("/api/customers", cts.Token);

        if (clientHangsUp)
        {
            await reached.Task;
            await cts.CancelAsync();
        }

        HttpStatusCode? status = null;
        try { status = (await call).StatusCode; }
        catch (OperationCanceledException) { /* the client hung up on purpose */ }

        // The server finishes the aborted request after the client has gone.
        await Task.Delay(250);
        return (status, log);
    }

    [Fact]
    public async Task ABrowserThatHangsUp_IsNotAnApplicationError_AndIsNotA500()
    {
        var reached = new TaskCompletionSource();
        var (status, log) = await RunApiPipelineAsync(
            async context =>
            {
                reached.SetResult();
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(20);
                }

                // EF Core's shape: its own cancelled token, never the request's.
                throw new TaskCanceledException("A task was canceled.", null, new CancellationToken(canceled: true));
            },
            clientHangsUp: true,
            reached);

        // Nothing was sent, because there is nobody left to send it to.
        Assert.Null(status);
        Assert.NotEqual(HttpStatusCode.InternalServerError, status);
        Assert.Empty(log.AtOrAbove(LogLevel.Warning));
        Assert.Contains(log.Entries, e => e.Message.Contains("aborted by the client", StringComparison.OrdinalIgnoreCase));
        // 499 is the status the request is recorded as finishing with.
        Assert.Contains(log.Entries, e => e.Message.Contains(" 499 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACancellationTheClientDidNotCause_IsStillAnError_AndStillA500()
    {
        var reached = new TaskCompletionSource();
        var (status, log) = await RunApiPipelineAsync(
            _ => throw new OperationCanceledException("the command timed out", new CancellationToken(canceled: true)),
            clientHangsUp: false,
            reached);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Contains(log.AtOrAbove(LogLevel.Error), m => m.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A SQL timeout arrives as a provider exception, not a cancellation, so it can never be mistaken for a client hanging up.</summary>
    [Fact]
    public async Task AProviderTimeout_IsStillAnError_AndStillA500()
    {
        var reached = new TaskCompletionSource();
        var (status, log) = await RunApiPipelineAsync(
            _ => throw new TimeoutException("Execution Timeout Expired."),
            clientHangsUp: false,
            reached);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Contains(log.AtOrAbove(LogLevel.Error), m => m.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The repository keeps passing the token to EF Core: a cancelled request
    /// stops the query rather than letting it run on for a caller who has
    /// gone. Removing the token from these queries would "fix" the exception
    /// by doing the work anyway, which is the opposite of what is wanted.
    /// </summary>
    [Fact]
    public async Task TheDirectoryQuery_StillHonoursTheCallersToken()
    {
        using var db = new DashboardSqliteFixture();
        using var context = db.CreateContext();
        var repository = new CustomerDirectoryRepository(context);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.ListAsync(new CustomerDirectoryQuery(null, null, null, null, false, 1, 25), cancelled.Token));
    }

    /// <summary>EF Core does not log a cancelled command as a failed one, so an abort leaves no error trail of its own.</summary>
    [Fact]
    public async Task EfCore_DoesNotLogACancelledCommandAsAFailure()
    {
        using var db = new DashboardSqliteFixture();
        var log = new CapturingProvider();
        using var loggerFactory = LoggerFactory.Create(b => { b.AddProvider(log); b.SetMinimumLevel(LogLevel.Trace); });

        using var seed = db.CreateContext();
        await using var context = new CancellingContext(
            new DbContextOptionsBuilder<TigerCS.Infrastructure.Persistence.TigerCsDbContext>()
                .UseSqlite(seed.Database.GetDbConnection())
                .UseLoggerFactory(loggerFactory)
                .AddInterceptors(new CancelInFlight())
                .Options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Tickets.CountAsync(CancellationToken.None));
        Assert.Empty(log.AtOrAbove(LogLevel.Warning));
    }

    private sealed class CancellingContext(DbContextOptions<TigerCS.Infrastructure.Persistence.TigerCsDbContext> options)
        : TigerCS.Infrastructure.Persistence.TigerCsDbContext(options);

    /// <summary>Fails a command the way one cancelled while already in flight fails.</summary>
    private sealed class CancelInFlight : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException("A task was canceled.", new CancellationToken(canceled: true));
    }

    /// <summary>The Customers filter form coalesces rapid changes and drops a repeat submit, so a second navigation never aborts the first.</summary>
    [Fact]
    public void TheCustomersFilterForm_CannotStartTwoNavigationsAtOnce()
    {
        var js = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "wwwroot", "js", "site.js")));

        // Rapid filter changes become one navigation, not one per change.
        Assert.Contains("AUTOSUBMIT_QUIET_MS", js, StringComparison.Ordinal);
        Assert.Contains("clearTimeout(autoSubmitTimers.get(form))", js, StringComparison.Ordinal);

        // A repeat submit is cancelled, covering requestSubmit() which never
        // touches the submit button the old guard disabled.
        Assert.Contains("form.dataset.submitting === \"true\"", js, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault()", js, StringComparison.Ordinal);

        // Returning through the Back button releases the form again.
        Assert.Contains("pageshow", js, StringComparison.Ordinal);

        // The form itself is a plain GET that still works with no script.
        var page = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "Customers.cshtml")));
        Assert.Contains("<form class=\"filter-bar\" id=\"customerFilters\" method=\"get\" action=\"/Customers\">", page, StringComparison.Ordinal);
        Assert.Contains("<button type=\"submit\" class=\"btn btn-sm\">Apply</button>", page, StringComparison.Ordinal);
    }

    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }
}
