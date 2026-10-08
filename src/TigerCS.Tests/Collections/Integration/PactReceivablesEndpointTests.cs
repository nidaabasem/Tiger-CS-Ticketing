using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Collections.Integration;

public sealed class PactReceivablesEndpointTests
{
    private sealed class Source : IPactReceivablesSource
    {
        public int Reads { get; private set; }
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly businessDate, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new PactReceivablesSnapshot([
                new PactReceivableInstalment(4, "3001", "PACT-only customer", "971500003001", "", 100,
                    "TP140-100", "", "INV-100", "", businessDate.AddDays(-businessDate.Day).ToDateTime(TimeOnly.MinValue), 123m, "Installment")
            ], DateTime.UtcNow, true));
        }
    }

    private static TigerCsApiFactory Factory(Source source) => new()
    {
        ExtraConfiguration = new()
        {
            ["Collections:Enabled"] = "true",
            ["CollectionsSource:PactReceivables:Enabled"] = "true"
        },
        ExtraServices = services => services.AddScoped<IPactReceivablesSource>(_ => source)
    };

    private static async Task<HttpClient> Client(TigerCsApiFactory factory, string role)
    {
        var (username, password, _) = await factory.SeedEmployeeAsync(role);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    [Fact]
    public async Task SystemAdministratorCanReadThroughCentralOverride_WithNoCustomerTicket()
    {
        var source = new Source();
        using var factory = Factory(source);
        using var client = await Client(factory, Roles.SystemAdministrator);
        var response = await client.GetAsync("/api/collections/receivables/customers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<PactReceivableCustomersDto>();
        Assert.Equal("3001", Assert.Single(report!.Items).TenantId);
        Assert.Equal(123m, report.Items[0].OverdueAmount);
    }

    [Fact]
    public async Task AnonymousAndReportingUsersCannotScanPACT()
    {
        var source = new Source();
        using var factory = Factory(source);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/collections/receivables/customers")).StatusCode);
        using var reporter = await Client(factory, Roles.ReportingUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.GetAsync("/api/collections/receivables/customers")).StatusCode);
        Assert.Equal(0, source.Reads);
    }

    private sealed class Catalog : ICollectionsTowerCatalog
    {
        public int Reads { get; private set; }
        public Task<IReadOnlyList<CollectionsTowerDto>> ListActiveAsync(CancellationToken cancellationToken)
        { Reads++; return Task.FromResult<IReadOnlyList<CollectionsTowerDto>>([new CollectionsTowerDto(3, "127", "Faradis", 32, true)]); }
    }

    private sealed class RecordingSource : IPactReceivablesSource
    {
        public PactReceivablesRequest? Request { get; private set; }
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) =>
            ReadAsync(new PactReceivablesRequest(null, throughDate), cancellationToken);
        public Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.TowerId == 404) throw new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.");
            return Task.FromResult(new PactReceivablesSnapshot([], DateTime.UtcNow, false));
        }
    }

    [Fact]
    public async Task TowersEndpoint_IsAuthorizedForFinancialReaders_AndDeniedToReportingUsers()
    {
        var catalog = new Catalog();
        using var factory = new TigerCsApiFactory
        {
            ExtraConfiguration = new() { ["Collections:Enabled"] = "true", ["CollectionsSource:PactReceivables:Enabled"] = "true" },
            ExtraServices = services => services.AddScoped<ICollectionsTowerCatalog>(_ => catalog)
        };
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/collections/receivables/towers")).StatusCode);
        using var reporter = await Client(factory, Roles.ReportingUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.GetAsync("/api/collections/receivables/towers")).StatusCode);
        Assert.Equal(0, catalog.Reads);
        using var admin = await Client(factory, Roles.SystemAdministrator);
        var towers = await (await admin.GetAsync("/api/collections/receivables/towers")).Content.ReadFromJsonAsync<List<CollectionsTowerDto>>();
        Assert.Equal("127 - Faradis", Assert.Single(towers!).Label);
    }

    [Fact]
    public async Task TowerAndDatesReachTheSource_AndAnUnknownTowerIsABadRequest()
    {
        var source = new RecordingSource();
        using var factory = new TigerCsApiFactory
        {
            ExtraConfiguration = new() { ["Collections:Enabled"] = "true", ["CollectionsSource:PactReceivables:Enabled"] = "true" },
            ExtraServices = services => services.AddScoped<IPactReceivablesSource>(_ => source)
        };
        using var client = await Client(factory, Roles.SystemAdministrator);
        var ok = await client.GetAsync("/api/collections/receivables/customers?towerId=3&dateFrom=2026-01-01&dateTo=2026-10-31&year=2026&month=10");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(new PactReceivablesRequest(new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31), null, 3, PactReceivableClass.DueOrOverdue, new DateOnly(2026, 10, 1)), source.Request);
        var report = await ok.Content.ReadFromJsonAsync<PactReceivableCustomersDto>();
        Assert.Equal((3, new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31)), (report!.TowerId, report.DateFrom, report.DateTo));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/collections/receivables/customers?towerId=404")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/collections/receivables/customers?dateFrom=2026-05-02&dateTo=2026-05-01")).StatusCode);
    }

    private sealed class Loader : IReceivablesRangeLoader
    {
        public List<(DateOnly, DateOnly)> Requests { get; } = [];
        public Task<bool> EnqueueAsync(DateOnly from, DateOnly through, CancellationToken cancellationToken) { Requests.Add((from, through)); return Task.FromResult(true); }
    }

    private sealed class GapSource(bool covered) : IPactReceivablesSource
    {
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => ReadAsync(new PactReceivablesRequest(null, throughDate), cancellationToken);
        public Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var company = new SnapshotCompanyStatusDto(4, "Tiger Group Dubai", true, now, now, "Succeeded", null, 0, 1, new DateOnly(2026, 1, 1), new DateOnly(2099, 12, 31), 0, 0m, 0, 0m, 0, 0, "Fresh", 0);
            var status = new SnapshotStatusDto([company], [], 90, request.FromDate, request.ThroughDate,
                covered ? [] : [new CoverageGapDto(4, "Tiger Group Dubai", request.FromDate!.Value, new DateOnly(2025, 12, 31))]);
            return Task.FromResult(new PactReceivablesSnapshot([], now, false, status));
        }
    }

    [Fact]
    public async Task CoverageLoadEndpoint_StartsABackgroundLoad_OnlyForAuthorizedCallers_AndOnlyWhenNeeded()
    {
        var loader = new Loader();
        var covered = false;
        using var factory = new TigerCsApiFactory
        {
            ExtraConfiguration = new() { ["Collections:Enabled"] = "true", ["CollectionsSource:PactReceivables:Enabled"] = "true" },
            ExtraServices = services =>
            {
                services.AddScoped<IReceivablesRangeLoader>(_ => loader);
                services.AddScoped<IPactReceivablesSource>(_ => new GapSource(covered));
            }
        };
        const string url = "/api/collections/receivables/coverage/load?dateFrom=2025-09-15&dateTo=2026-03-15";
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(url, null)).StatusCode);
        using var reporter = await Client(factory, Roles.ReportingUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.PostAsync(url, null)).StatusCode);
        Assert.Empty(loader.Requests);
        using var admin = await Client(factory, Roles.SystemAdministrator);
        var accepted = await admin.PostAsync(url, null);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.True((await accepted.Content.ReadFromJsonAsync<ReceivablesRangeLoadDto>())!.Accepted);
        Assert.Equal((new DateOnly(2025, 9, 15), new DateOnly(2026, 3, 15)), Assert.Single(loader.Requests));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/collections/receivables/coverage/load?dateFrom=2026-03-15&dateTo=2025-09-15", null)).StatusCode);
        covered = true;
        var nothing = await admin.PostAsync(url, null);
        Assert.Equal(HttpStatusCode.OK, nothing.StatusCode);
        Assert.True((await nothing.Content.ReadFromJsonAsync<ReceivablesRangeLoadDto>())!.AlreadyCovered);
        Assert.Single(loader.Requests);
    }
}
