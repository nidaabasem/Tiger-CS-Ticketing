using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Collections.Services;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>Scripted Genesys: records every upload exactly as the service sent it and answers from a queue of outcomes.</summary>
public sealed class FakeGenesysClient : IGenesysOutboundClient
{
    public sealed record Call(string ContactListId, IReadOnlyList<GenesysContactPayload> Contacts);
    private readonly object _gate = new();
    public List<Call> Calls { get; } = [];
    public Queue<GenesysUploadOutcome> Script { get; } = new();
    /// <summary>For the next Accepted upload only: return this many contact ids instead of one per contact (null entry = one per contact).</summary>
    public Queue<int?> IdCounts { get; } = new();
    public int Created;

    /// <summary>Every suppression (PUT callable=false) request, as the service sent it.</summary>
    public sealed record Suppression(string ContactListId, string ContactId, GenesysContactPayload Contact);
    public List<Suppression> Suppressions { get; } = [];
    public Queue<GenesysSuppressResult> SuppressScript { get; } = new();

    public Task<GenesysSuppressResult> SetNotCallableAsync(string contactListId, string contactId, GenesysContactPayload contact, CancellationToken ct)
    {
        lock (_gate)
        {
            Suppressions.Add(new(contactListId, contactId, contact));
            return Task.FromResult(SuppressScript.Count > 0 ? SuppressScript.Dequeue() : new GenesysSuppressResult(GenesysUploadOutcome.Accepted, 200, false, false, null));
        }
    }

    public Task<GenesysUploadResult> UploadContactsAsync(string contactListId, IReadOnlyList<GenesysContactPayload> contacts, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add(new(contactListId, contacts.ToList()));
            var outcome = Script.Count > 0 ? Script.Dequeue() : GenesysUploadOutcome.Accepted;
            return Task.FromResult(outcome switch
            {
                GenesysUploadOutcome.Accepted => new GenesysUploadResult(outcome, 200,
                    contacts.Take(IdCounts.Count > 0 ? IdCounts.Dequeue() ?? contacts.Count : contacts.Count).Select(_ => $"contact-{++Created}").ToList(), null),
                GenesysUploadOutcome.Rejected => new GenesysUploadResult(outcome, 400, [], "Genesys rejected the request (HTTP 400); nothing was created."),
                _ => new GenesysUploadResult(outcome, null, [], "The request timed out or the connection dropped before Genesys answered.")
            });
        }
    }
}

public sealed class FakeJobScheduler : IReviewJobScheduler
{
    public List<long> Refreshes { get; } = [];
    public List<long> Dispatches { get; } = [];
    public void EnqueueRefresh(long runId) => Refreshes.Add(runId);
    public void EnqueueDispatch(long dispatchId) => Dispatches.Add(dispatchId);
    public int Sweeps;
    public void EnqueueSuppressionSweep() => Sweeps++;
}

/// <summary>
/// The real EF store and services over a real relational database (SQLite), with scripted PACT and Genesys. In-memory by
/// default; <c>file: true</c> gives each scope its own connection so concurrent calls really race on the database.
/// </summary>
internal sealed class ReviewHarness : IDisposable
{
    private sealed class SqliteTigerCsDbContext(DbContextOptions<TigerCsDbContext> options) : TigerCsDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Ticket>().Property(t => t.RowVersion).HasDefaultValueSql("X'0000000000000000'");
            builder.Entity<TicketAgentHandoff>().Property(h => h.RowVersion).HasDefaultValueSql("X'0000000000000000'");
        }
    }

    /// <summary>A day on which Current Month (day 14) is scheduled, noon in Dubai.</summary>
    public static readonly DateTime Now = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection? _shared;
    private readonly string? _file;

    public CollectionsCampaignAppServiceTests.Source Source { get; } = new() { ReadAt = Now };
    public CollectionsOptions Options { get; } = new() { Enabled = true };
    public PactReceivablesOptions Sql { get; } = new() { Enabled = true };
    public CollectionsCampaignOptions Campaign { get; } = new() { FinancialSourceValidated = true, LegalNoticeExportEnabled = true };
    public CollectionsReviewOptions Review { get; } = new();
    public GenesysOutboundOptions Genesys { get; } = new() { Enabled = true, LiveCustomerDispatchEnabled = true, SuppressionEnabled = true, LeaseHeartbeatSeconds = 0.02 };
    public FakeGenesysClient Client { get; } = new();
    public FakeJobScheduler Scheduler { get; } = new();
    public FakeTimeProvider Time { get; } = new(Now);
    public CollectionsCaller Manager { get; } = new(Guid.NewGuid(), [Roles.CsManager], []);
    /// <summary>May read balances but not send.</summary>
    public CollectionsCaller Agent { get; } = new(Guid.NewGuid(), [Roles.CsAgent], []);
    /// <summary>No Collections grant at all.</summary>
    public CollectionsCaller Reporting { get; } = new(Guid.NewGuid(), [Roles.ReportingUser], []);

    public ReviewHarness(bool file = false)
    {
        if (file)
        {
            _file = Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}.db");
        }
        else
        {
            _shared = new SqliteConnection("DataSource=:memory:");
            _shared.Open();
        }
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public TigerCsDbContext CreateContext() => new SqliteTigerCsDbContext(
        (_shared is not null
            ? new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlite(_shared)
            : new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlite($"Data Source={_file};Pooling=False;Default Timeout=60"))
        .Options);

    public sealed class Scope(TigerCsDbContext context, ReviewHarness h) : IDisposable
    {
        public IReviewStore Store { get; } = new ReviewStore(context);
        public ReviewQueryService Query { get; private set; } = null!;
        public ReviewRefreshService Refresh { get; private set; } = null!;
        public DispatchService Dispatch { get; private set; } = null!;
        public SuppressionService Suppression { get; private set; } = null!;
        public TigerCsDbContext Context => context;

        public Scope Wire(IGenesysOutboundClient? client = null)
        {
            var clock = new CollectionsClock(h.Options, h.Time);
            var auth = new CollectionsAuthorizationService(h.Options, new FakeDepartmentRepository());
            Query = new ReviewQueryService(h.Options, h.Review, auth, clock, Store);
            Refresh = new ReviewRefreshService(h.Options, h.Campaign, h.Sql, h.Review, auth, clock, h.Source, Store, h.Scheduler, Query,
                NullLogger<ReviewRefreshService>.Instance);
            var balances = new CurrentBalanceReader(h.Sql, h.Source, Refresh, NullLogger<CurrentBalanceReader>.Instance);
            Dispatch = new DispatchService(h.Options, h.Genesys, auth, clock, Store, h.Scheduler, client ?? h.Client, Query, balances,
                NullLogger<DispatchService>.Instance);
            Suppression = new SuppressionService(h.Options, h.Genesys, clock, Store, client ?? h.Client, balances, NullLogger<SuppressionService>.Instance);
            return this;
        }

        public void Dispose() => context.Dispose();
    }

    public Scope NewScope(IGenesysOutboundClient? client = null) => new Scope(CreateContext(), this).Wire(client);

    /// <summary>Starts and runs a refresh job, the way Hangfire would, and returns the published run.</summary>
    public async Task<ReviewRunDto> RefreshAsync()
    {
        using var scope = NewScope();
        var started = await scope.Refresh.StartAsync(Manager, null, CancellationToken.None);
        Assert.True(started.IsSuccess, started.Detail);
        await scope.Refresh.RunAsync(started.Value!.RunId, CancellationToken.None);
        return (await scope.Query.GetRunAsync(Manager, started.Value.RunId, CancellationToken.None)).Value!;
    }

    /// <summary>A Ready-able V2 source row: remaining equals plan (Unpaid), valid UAE mobile, one unit per tenant.</summary>
    public static PactReceivableInstalment Row(int n, decimal amount = 500m, int day = 20, string status = "Installment", decimal? plan = null,
        int company = 4, string? mobile = null, string? name = null, string project = "TP140") => new(company, $"T{n:D4}", name ?? $"Customer {n}",
            mobile ?? $"97150{n:D7}", $"c{n}@example.test", 1000 + n, $"TP140-{n}", project, $"INV-{n}", "",
            new DateTime(2026, 10, day), amount, status, plan ?? amount);

    public SelectionRequest All(ReviewFilter? filter = null) => new(filter ?? new ReviewFilter(), ReviewQueryService.ModeAllMatching);

    public async Task<(SelectionSummaryDto Summary, ConfirmDispatchRequest Confirm)> PrepareAsync(SelectionRequest? selection = null, string key = "k1")
    {
        using var scope = NewScope();
        var request = selection ?? All(new ReviewFilter(ReminderType: "CurrentMonth"));
        var summary = (await scope.Query.SummarizeAsync(Manager, request, CancellationToken.None)).Value!;
        return (summary, new ConfirmDispatchRequest(request, summary.Count, summary.Fingerprint, key, true));
    }

    public void Dispose()
    {
        _shared?.Dispose();
        SqliteConnection.ClearAllPools();
        if (_file is not null) try { File.Delete(_file); } catch (IOException) { }
    }
}
