extern alias TigerCsWeb;

using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// Protects against TigerCS.Web -> TigerCS.Api communication drifting apart:
/// every typed Api client (<see cref="ApiClientBase"/>) must resolve its
/// <see cref="HttpClient.BaseAddress"/> from the single configured
/// <c>TigerCsApi:BaseUrl</c> source that <c>Program.cs</c> binds, never a
/// hard-coded or per-client address. Spins up TigerCS.Web's real host
/// (<c>Microsoft.AspNetCore.TestHost</c> — no real socket, no database) so
/// this exercises the actual DI registrations in <c>Program.cs</c> itself,
/// not a hand-rolled copy of them that could drift from the real thing.
/// </summary>
public sealed class ApiClientConfigurationTests
{
    private static readonly PropertyInfo HttpProperty =
        typeof(ApiClientBase).GetProperty("Http", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("ApiClientBase.Http property not found — has it been renamed?");

    private static WebApplicationFactory<TigerCsWeb::Program> CreateFactory() =>
        new WebApplicationFactory<TigerCsWeb::Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

    private static HttpClient GetHttpClient(object apiClient) => (HttpClient)HttpProperty.GetValue(apiClient)!;

    [Fact]
    public void ConfiguredBaseUrl_IsBoundFromTheTigerCsApiSection_AndIsNotEmpty()
    {
        using var factory = CreateFactory();

        var options = factory.Services.GetRequiredService<IOptions<TigerCsApiOptions>>().Value;

        Assert.False(string.IsNullOrWhiteSpace(options.BaseUrl));
    }

    [Theory]
    [InlineData(typeof(AuthApiClient))]
    [InlineData(typeof(TicketsApiClient))]
    [InlineData(typeof(TicketSlaApiClient))]
    [InlineData(typeof(UsersApiClient))]
    [InlineData(typeof(IntakeRecordsApiClient))]
    [InlineData(typeof(CustomerLookupApiClient))]
    [InlineData(typeof(CategoriesApiClient))]
    [InlineData(typeof(DepartmentsApiClient))]
    public void EveryTypedApiClient_ResolvesTheSameConfiguredBaseAddress_NeverAHardcodedOne(Type clientType)
    {
        using var factory = CreateFactory();
        var expected = new Uri(factory.Services.GetRequiredService<IOptions<TigerCsApiOptions>>().Value.BaseUrl);

        using var scope = factory.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService(clientType);

        Assert.Equal(expected, GetHttpClient(client).BaseAddress);
    }

    [Fact]
    public void DepartmentsApiClient_And_IntakeRecordsApiClient_NeverDriftToDifferentBaseAddresses()
    {
        // The two clients directly behind the reported bug's two symptoms
        // ("Unable to load the department list" / "Could not record this
        // interaction") must always share exactly one configured address.
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();

        var departmentsBaseAddress = GetHttpClient(scope.ServiceProvider.GetRequiredService<DepartmentsApiClient>()).BaseAddress;
        var intakeBaseAddress = GetHttpClient(scope.ServiceProvider.GetRequiredService<IntakeRecordsApiClient>()).BaseAddress;

        Assert.Equal(departmentsBaseAddress, intakeBaseAddress);
    }
}
