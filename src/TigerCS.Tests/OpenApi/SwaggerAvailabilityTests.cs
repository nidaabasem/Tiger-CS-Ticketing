using System.Net;
using Microsoft.OpenApi.Reader;

namespace TigerCS.Tests.OpenApi;

/// <summary>
/// Where the Swagger surface is, and is not, reachable
/// (OpenApiDocumentation.EnabledEnvironments).
/// </summary>
public class SwaggerAvailabilityTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task SwaggerUi_IsServedAt_Swagger(string environment)
    {
        using var factory = new SwaggerApiFactory(environment);
        using var client = factory.CreateClient();

        // "/swagger" itself 301s to "/swagger/index.html"; the default client
        // follows that, so this asserts the whole documented entry point.
        var response = await client.GetAsync("/swagger");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("swagger-ui", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task SwaggerJson_IsServedAt_SwaggerV1SwaggerJson(string environment)
    {
        using var factory = new SwaggerApiFactory(environment);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/swagger")]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger/v1/swagger.json")]
    public async Task Swagger_IsNotExposed_InProduction(string path)
    {
        using var factory = new SwaggerApiFactory("Production");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        // Nothing Swagger-related is mapped outside the enabled environments,
        // so these paths behave exactly like any route this application does
        // not have. (That is not a 404: AddTigerCsAuthorizationPolicies sets a
        // fallback policy, which the authorization middleware applies to
        // unmatched requests too — so an unknown path is answered 401. This
        // asserts "indistinguishable from a route that does not exist", which
        // is the property that matters, without pinning the status code that
        // pre-existing policy happens to produce.)
        var unmappedControlPath = await client.GetAsync("/this-route-does-not-exist");
        Assert.Equal(unmappedControlPath.StatusCode, response.StatusCode);
    }

    [Fact]
    public async Task ProductionHost_StillServesItsRealEndpoints()
    {
        // Guards the test above from passing for the wrong reason: if the
        // Production host were simply broken, every path would look "not
        // exposed". /health is anonymous and must still answer 200.
        using var factory = new SwaggerApiFactory("Production");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LegacyOpenApiRoute_StillServesTheSameDocument()
    {
        // MapOpenApi()'s pre-Swagger default route, kept so anything already
        // pointed at it keeps working.
        using var factory = new SwaggerApiFactory("Development");
        using var client = factory.CreateClient();

        var legacy = await client.GetAsync("/openapi/v1.json");
        var swagger = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal(
            await swagger.Content.ReadAsStringAsync(),
            await legacy.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LegacyOpenApiRoute_IsNotExposed_InProduction()
    {
        using var factory = new SwaggerApiFactory("Production");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerJson_RequiresNoAccessToken()
    {
        using var factory = new SwaggerApiFactory("Development");
        using var client = factory.CreateClient();

        // The fallback authorization policy requires an authenticated user for
        // every endpoint that does not opt out; the document route opts out, so
        // Swagger UI can load it before the user has signed in.
        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerJson_IsAValidOpenApiDocument()
    {
        using var factory = new SwaggerApiFactory("Development");
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/swagger/v1/swagger.json");
        var result = OpenApiModelFactory.Parse(json, "json");

        Assert.Empty(result.Diagnostic?.Errors ?? []);
        Assert.NotNull(result.Document);
        Assert.Equal("Tiger CS Ticketing API", result.Document!.Info.Title);
        Assert.Equal("v1", result.Document.Info.Version);
        Assert.NotEmpty(result.Document.Paths);
    }
}
