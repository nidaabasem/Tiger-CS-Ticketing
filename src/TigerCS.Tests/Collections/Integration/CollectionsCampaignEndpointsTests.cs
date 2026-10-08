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

public sealed class CollectionsCampaignEndpointsTests
{
    private sealed class Source : IPactReceivablesSource
    {
        public int Reads { get; private set; }
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new PactReceivablesSnapshot([
                new(4, "3001", "Campaign Customer", "971500003001", "", 101, "TP140-101", "TP140", "INV", "",
                    throughDate.ToDateTime(TimeOnly.MinValue), 600m, "Installment")
            ], DateTime.UtcNow, false));
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
    public async Task SystemAdministratorCanPreviewAndExportReview_ThroughCentralOverride()
    {
        var source = new Source(); using var factory = Factory(source);
        using var client = await Client(factory, Roles.SystemAdministrator);
        var preview = await client.GetAsync("/api/collections/campaigns/preview?stage=CurrentMonthReminder");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.True(preview.Headers.CacheControl!.NoStore);
        Assert.Equal("Campaign Customer", Assert.Single((await preview.Content.ReadFromJsonAsync<CollectionsCampaignPreviewDto>())!.Items).CustomerName);
        var export = await client.GetAsync("/api/collections/campaigns/export?stage=CurrentMonthReminder&mode=review");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("InternalReviewOnly", (await export.Content.ReadFromJsonAsync<CollectionsCampaignExportDto>())!.Csv);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task AnonymousAndReportingUserCannotPreview_AgentCannotExport_WithoutSourceRead()
    {
        var source = new Source(); using var factory = Factory(source);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/collections/campaigns/preview?stage=CurrentMonthReminder")).StatusCode);
        using var reporter = await Client(factory, Roles.ReportingUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporter.GetAsync("/api/collections/campaigns/preview?stage=CurrentMonthReminder")).StatusCode);
        using var agent = await Client(factory, Roles.CsAgent);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/collections/campaigns/export?stage=CurrentMonthReminder&mode=review")).StatusCode);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task InvalidDateIsRejectedAtApiEdge_AndUnsafeCampaignExportIsRefused()
    {
        var source = new Source(); using var factory = Factory(source);
        using var client = await Client(factory, Roles.CsManager);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/collections/campaigns/preview?stage=CurrentMonthReminder&businessDate=invalid")).StatusCode);
        Assert.Equal(0, source.Reads);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/collections/campaigns/export?stage=CurrentMonthReminder&mode=genesys")).StatusCode);
        Assert.Equal(1, source.Reads);
    }
}
