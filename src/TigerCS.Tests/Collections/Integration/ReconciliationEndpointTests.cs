using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Reconciliation;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Collections.Integration;

/// <summary>The snapshot-status poll endpoint and the reconciliation endpoints: authorization, no side effects on read, a run only on an explicit POST.</summary>
public sealed class ReconciliationEndpointTests
{
    private sealed class StatusSource : IPactReceivablesSource, ISnapshotStatusSource
    {
        public int StatusReads { get; private set; }
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => throw new InvalidOperationException("a status poll must not read receivables");
        public Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("a status poll must not read receivables");
        public Task<SnapshotStatusDto> ReadStatusAsync(CancellationToken cancellationToken)
        {
            StatusReads++;
            var now = DateTime.UtcNow;
            var dubai = new SnapshotCompanyStatusDto(4, "Tiger Group Dubai", true, now.AddMinutes(-5), now.AddMinutes(-1), "Running", null, 0, 10, null, null, 0, 0m, 0, 0m, 0, 0, "Fresh", 5, true, now.AddMinutes(-1), "Running");
            return Task.FromResult(new SnapshotStatusDto([dubai], [], 180));
        }
    }

    private sealed class Launcher : IReconciliationLauncher
    {
        public List<Guid> Launched { get; } = [];
        public void Launch(Guid runId) => Launched.Add(runId);
    }

    private static TigerCsApiFactory Factory(StatusSource source, Launcher launcher) => new()
    {
        ExtraConfiguration = new() { ["Collections:Enabled"] = "true", ["CollectionsSource:PactReceivables:Enabled"] = "true" },
        ExtraServices = services =>
        {
            services.AddScoped<IPactReceivablesSource>(_ => source);
            services.AddSingleton<IReconciliationLauncher>(launcher);
        }
    };

    private static async Task<HttpClient> Client(TigerCsApiFactory factory, string role)
    {
        var (username, password, _) = await factory.SeedEmployeeAsync(role);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    [Fact]
    public async Task SnapshotStatusEndpoint_IsAuthorizedForFinancialReaders_AndReadsOnlyTheStatus()
    {
        var source = new StatusSource();
        using var factory = Factory(source, new Launcher());
        const string url = "/api/collections/receivables/snapshot/status";
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        using var reporter = await Client(factory, Roles.ReportingUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.GetAsync(url)).StatusCode);
        Assert.Equal(0, source.StatusReads);
        using var admin = await Client(factory, Roles.SystemAdministrator);
        var ok = await admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var status = (await ok.Content.ReadFromJsonAsync<SnapshotStatusDto>())!;
        Assert.True(status.LoadInProgress);
        Assert.Equal("Loading", status.Companies.Single().State);
    }

    [Fact]
    public async Task ReconciliationEndpoints_AreAuthorized_ReadingStartsNothing_AndARunNeedsAnExplicitPost()
    {
        var launcher = new Launcher();
        using var factory = Factory(new StatusSource(), launcher);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/collections/reconciliation")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/collections/reconciliation/run", new { })).StatusCode);
        using var reporter = await Client(factory, Roles.ReportingUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.GetAsync("/api/collections/reconciliation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.PostAsJsonAsync("/api/collections/reconciliation/run", new { })).StatusCode);
        Assert.Empty(launcher.Launched);

        using var admin = await Client(factory, Roles.SystemAdministrator);
        var overview = await admin.GetAsync("/api/collections/reconciliation");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        var body = (await overview.Content.ReadFromJsonAsync<ReconciliationOverviewDto>())!;
        Assert.Equal(SourceGate.SignOffMissing, body.Gate.Code);          // committed configuration: no sign-off
        Assert.False(body.Gate.Reconciled);
        Assert.Empty(launcher.Launched);                                  // reading never starts a run

        var started = await admin.PostAsJsonAsync("/api/collections/reconciliation/run", new { companyId = 4 });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<ReconciliationRunDto>())!;
        Assert.Equal(run.RunId, Assert.Single(launcher.Launched));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/collections/reconciliation/run", new { companyId = 4 })).StatusCode);   // single flight
        Assert.Single(launcher.Launched);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/collections/reconciliation/runs/{run.RunId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/collections/reconciliation/runs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/collections/reconciliation/run", new { companyId = 7 })).StatusCode);
    }
}
