using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Tests.Web.Fakes;
using TigerCS.Web;
using TigerCS.Web.Infrastructure;

namespace TigerCS.Tests.Web;

/// <summary>
/// Hosts TigerCS.Web in-process with a scripted API behind it.
///
/// <para>
/// Targets <see cref="WebHostMarker"/> rather than <c>Program</c>: TigerCS.Api's
/// own top-level statements already own that name in this test project.
/// </para>
/// </summary>
public sealed class TigerCsWebFactory : WebApplicationFactory<WebHostMarker>
{
    /// <summary>The API stub. Script it before creating a client.</summary>
    public StubApiHandler Api { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Replacing the web root with a non-watching provider has to happen on
        // the environment itself: the asp-append-version tag helper resolves
        // IWebHostEnvironment.WebRootFileProvider directly, not the static-file
        // options.
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(
            new WebRootFileProviderReplacementFilter()));

        return base.CreateHost(builder);
    }

    /// <summary>Swaps the watching web-root provider for a null one, once, at startup.</summary>
    private sealed class WebRootFileProviderReplacementFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            var environment = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
            environment.WebRootFileProvider = new NullFileProvider();
            next(app);
        };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // No file watching in tests. The host would otherwise open an inotify
        // instance per configuration source, and the asp-append-version tag
        // helper opens another on the web root - a suite that stands up dozens
        // of hosts exhausts the per-user limit and fails on an environment
        // resource, not on anything the application did.
        builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Api:BaseUrl"] = "https://api.tigercs.test",
                ["Api:Timeout"] = "00:00:30"
            }));

        builder.ConfigureServices(services =>
        {
            // Static assets are not under test here, and a watching file
            // provider is what asp-append-version opens an inotify handle for.
            services.Configure<Microsoft.AspNetCore.Builder.StaticFileOptions>(options =>
                options.FileProvider = new Microsoft.Extensions.FileProviders.NullFileProvider());

            // Replaces only the transport. The delegating handler that attaches
            // the bearer token and the correlation id stays in the pipeline, so
            // these tests still exercise the real authentication plumbing.
            services
                .AddHttpClient<TigerCsApiClient>()
                .ConfigurePrimaryHttpMessageHandler(() => Api);
        });
    }

    /// <summary>
    /// A client that speaks https and does not follow redirects.
    ///
    /// <para>
    /// https matters: the authentication cookie is <c>Secure</c>, so over plain
    /// http the browser-equivalent handler would drop it and every signed-in
    /// assertion would fail for the wrong reason. Not following redirects
    /// matters because the redirect itself is frequently the thing under test.
    /// </para>
    /// </summary>
    public HttpClient CreateWebClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });
}
