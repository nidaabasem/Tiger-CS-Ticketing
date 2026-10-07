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
                    "TP140-100", "", "INV-100", "", businessDate.AddDays(-1).ToDateTime(TimeOnly.MinValue), 123m, "Installment")
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
}
