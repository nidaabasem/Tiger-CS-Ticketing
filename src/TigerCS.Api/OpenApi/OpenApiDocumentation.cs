namespace TigerCS.Api.OpenApi;

/// <summary>
/// Registration and endpoint wiring for the API's Swagger/OpenAPI surface.
///
/// <para>
/// The OpenAPI <i>document</i> is produced by the framework's own .NET 10
/// generator (<c>Microsoft.AspNetCore.OpenApi</c>); Swashbuckle contributes
/// the Swagger <i>UI</i> only. Keeping the two responsibilities split avoids
/// running two document generators over the same endpoints.
/// </para>
/// </summary>
public static class OpenApiDocumentation
{
    /// <summary>The OpenAPI document name, and the <c>{documentName}</c> segment of the JSON route below.</summary>
    public const string DocumentName = "v1";

    /// <summary>Where the generated OpenAPI JSON is served.</summary>
    public const string DocumentRoute = "/swagger/{documentName}/swagger.json";

    /// <summary>
    /// The route <c>MapOpenApi()</c>'s default served before Swagger UI was
    /// added. Kept mapped so anything already pointed at it keeps working;
    /// it serves the same document as <see cref="DocumentRoute"/>.
    /// </summary>
    public const string LegacyDocumentRoute = "/openapi/{documentName}.json";

    /// <summary>Where Swagger UI is served (as a path prefix, hence no leading slash).</summary>
    public const string SwaggerUiRoutePrefix = "swagger";

    /// <summary>
    /// Environments in which the Swagger UI and the OpenAPI JSON are exposed.
    /// An explicit allow-list, not a "not Production" deny-list, so a future
    /// environment name (a real "UAT"/"Staging") stays closed by default
    /// rather than being exposed because nobody remembered to add it to a
    /// deny-list — the same reasoning as
    /// <c>CrmGatewaySafety.MockAllowedEnvironments</c>. "Testing" is the
    /// environment name the integration-test host uses.
    /// </summary>
    public static readonly IReadOnlyCollection<string> EnabledEnvironments = ["Development", "Testing"];

    /// <summary>True when <paramref name="environment"/> is one of <see cref="EnabledEnvironments"/>.</summary>
    public static bool IsEnabledFor(IHostEnvironment environment) =>
        EnabledEnvironments.Contains(environment.EnvironmentName);

    /// <summary>
    /// Registers the OpenAPI document generator together with this API's
    /// document/operation transformers. Registration is unconditional (it
    /// adds services, not endpoints); whether anything is actually reachable
    /// is decided by <see cref="MapTigerCsSwagger"/>.
    /// </summary>
    public static IServiceCollection AddTigerCsOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer<TigerCsOpenApiDocumentTransformer>();
            options.AddOperationTransformer<TigerCsOpenApiOperationTransformer>();
            options.AddSchemaTransformer<TigerCsOpenApiSchemaTransformer>();
        });

        return services;
    }

    /// <summary>
    /// Maps the Swagger UI at <c>/swagger</c> and the OpenAPI JSON at
    /// <c>/swagger/v1/swagger.json</c> (plus <see cref="LegacyDocumentRoute"/>)
    /// — but only in <see cref="EnabledEnvironments"/>.
    /// </summary>
    /// <remarks>
    /// Outside those environments nothing is mapped at all, rather than being
    /// mapped and then refused, so the paths behave exactly like routes this
    /// application does not have.
    /// </remarks>
    public static WebApplication MapTigerCsSwagger(this WebApplication app)
    {
        if (!IsEnabledFor(app.Environment))
        {
            return app;
        }

        app.MapOpenApi(DocumentRoute).AllowAnonymous();
        app.MapOpenApi(LegacyDocumentRoute).AllowAnonymous();

        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = SwaggerUiRoutePrefix;
            options.SwaggerEndpoint($"/swagger/{DocumentName}/swagger.json", "Tiger CS Ticketing API v1");
            options.DocumentTitle = "Tiger CS Ticketing API";
            options.DisplayRequestDuration();
        });

        return app;
    }
}
