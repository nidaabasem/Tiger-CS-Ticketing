using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Collections.Crm;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Collections.Integration;

public sealed class CollectionsUnitsEndpointsTests
{
    private sealed class Source : IPactReceivablesSource
    {
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) =>
            Task.FromResult(new PactReceivablesSnapshot([], DateTime.UtcNow, false));
    }

    private static TigerCsApiFactory Factory() => new()
    {
        ExtraConfiguration = new()
        {
            ["Collections:Enabled"] = "true",
            ["CollectionsSource:PactReceivables:Enabled"] = "true"
        },
        ExtraServices = services =>
        {
            services.AddScoped<IPactReceivablesSource>(_ => new Source());
            services.AddSingleton<ICollectionsCrmOwnerStore>(new FakeCrmOwnerStore());
        }
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
    public async Task SystemAdministrator_IsAuthorized_OnTheUnitSummaryAndCrmStatus_ThroughCentralOverride()
    {
        using var factory = Factory();
        using var admin = await Client(factory, Roles.SystemAdministrator);
        // The test source is not the snapshot source, so the unit summary reports it is unavailable (503), proving the caller got past authorization.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.GetAsync("/api/collections/units/payment-summary?unitCode=TP140-101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/collections/leasing/payment-summary?mobile=abc")).StatusCode);
        // Customer / unit linking: an unusable number is a 400; a CRM customer TigerCS holds no phone for is answered (NotSearched), never guessed.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/collections/customers/lookup?phone=abc")).StatusCode);
        var noPhone = await admin.GetAsync("/api/collections/customers/crm/498397/units");
        Assert.Equal(HttpStatusCode.OK, noPhone.StatusCode);
        var link = (await noPhone.Content.ReadFromJsonAsync<CustomerUnitLinkResultDto>())!;
        Assert.Equal("NotSearched", link.CrmStatus);
        Assert.Empty(link.Candidates);
        var status = await admin.GetAsync("/api/collections/crm-owners/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.False((await status.Content.ReadFromJsonAsync<CrmOwnerState>())!.Loaded);
    }

    [Fact]
    public async Task AnonymousAndReportingUsers_AreRefused_OnBothEndpoints()
    {
        using var factory = Factory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/collections/units/payment-summary?unitCode=TP140-101")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/collections/crm-owners/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/collections/leasing/payment-summary?mobile=0501234567")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/collections/customers/lookup?phone=0501234567")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/collections/customers/crm/498397/units")).StatusCode);
        using var reporter = await Client(factory, Roles.ReportingUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.GetAsync("/api/collections/units/payment-summary?unitCode=TP140-101")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.GetAsync("/api/collections/crm-owners/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.GetAsync("/api/collections/leasing/payment-summary?mobile=0501234567")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.GetAsync("/api/collections/customers/lookup?phone=0501234567")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.GetAsync("/api/collections/customers/crm/498397/units?phone=0501234567")).StatusCode);
    }
}
