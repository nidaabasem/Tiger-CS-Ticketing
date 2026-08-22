using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Web.Authorization;
using TigerCS.Web.Infrastructure;
using TigerCS.Web.Services;

namespace TigerCS.Web;

/// <summary>
/// The application's entry point, and the type TigerCS.Tests hands to
/// <c>WebApplicationFactory</c>.
///
/// <para>
/// Written as an explicit class rather than top-level statements on purpose.
/// In .NET 10 the compiler-generated <c>Program</c> class is public, so a
/// second set of top-level statements in this solution would collide with
/// TigerCS.Api's - <c>WebApplicationFactory&lt;Program&gt;</c> becomes
/// ambiguous the moment a test project references both assemblies, which
/// would break every existing API integration test. A named type in this
/// project's own namespace has no such collision.
/// </para>
/// </summary>
public sealed class WebHostMarker
{
    /// <summary>Configures and runs the Tiger Group Service Desk web application.</summary>
    /// <param name="args">Command-line arguments, passed through to the host builder.</param>
    public static void Main(string[] args)
    {
    var builder = WebApplication.CreateBuilder(args);

    // ---------------------------------------------------------------------------
    // Configuration
    // ---------------------------------------------------------------------------
    // Api:BaseUrl is the only setting this application needs, and it is not a
    // secret - it is an internal address, committed per environment. No credential
    // of any kind is committed here or read from configuration: every API call is
    // made with the signed-in user's own access token. There is no service
    // account, no shared key and no client secret anywhere in this project.
    builder.Services
        .AddOptions<ApiClientOptions>()
        .Bind(builder.Configuration.GetSection(ApiClientOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    // ---------------------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------------------
    // The API is JWT-bearer; this application is server-rendered. Rather than hand
    // a bearer token to the browser - where any script-injection defect would read
    // it out of localStorage or a readable cookie - the token is carried inside
    // this application's own authentication cookie:
    //
    //   * encrypted and signed by ASP.NET Core Data Protection, so the browser
    //     holds ciphertext it cannot read or forge;
    //   * HttpOnly, so no script can reach it even in the same origin;
    //   * SameSite=Strict and Secure, so it is neither sent cross-site nor over
    //     plaintext;
    //   * expiring no later than the token it carries, so a cookie can never
    //     outlive the credential inside it.
    //
    // The cookie is decrypted server-side, per request, only to set an
    // Authorization header on an outbound call. The token never reaches a view, a
    // log line, or a URL.
    //
    // This also decides the CSRF story: a cookie-authenticated POST is
    // forgeable cross-site, so anti-forgery validation is mandatory rather than
    // optional, and is applied globally below.
    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "TigerCS.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.IsEssential = true;

            options.LoginPath = "/Account/Login";
            options.LogoutPath = "/Account/Logout";
            options.AccessDeniedPath = "/Forbidden";
            options.ReturnUrlParameter = "returnUrl";

            // A fixed lifetime with no sliding renewal. Sliding expiration would
            // keep the cookie alive past the access token it carries, leaving a
            // signed-in-looking user whose every API call 401s. The sign-in path
            // sets the real expiry from the token's own ExpiresAtUtc.
            options.SlidingExpiration = false;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
        });

    builder.Services.AddAuthorization(options =>
        // Every page requires an authenticated user unless it opts out with
        // [AllowAnonymous]. A page added later is protected by default rather than
        // by the author remembering to protect it.
        options.FallbackPolicy = options.DefaultPolicy);

    // ---------------------------------------------------------------------------
    // Razor Pages
    // ---------------------------------------------------------------------------
    builder.Services
        .AddRazorPages(options =>
        {
            options.Conventions.AllowAnonymousToPage("/Account/Login");
            options.Conventions.AllowAnonymousToPage("/Error");
        })
        .AddMvcOptions(options =>
            // Anti-forgery validation on every non-GET request, not per-handler.
            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

    builder.Services.AddAntiforgery(options =>
    {
        options.Cookie.Name = "TigerCS.Antiforgery";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.HeaderName = "RequestVerificationToken";
    });

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddMemoryCache();

    // ---------------------------------------------------------------------------
    // API access
    // ---------------------------------------------------------------------------
    builder.Services.AddScoped<AccessTokenAccessor>();
    builder.Services.AddScoped<CorrelationIdAccessor>();
    builder.Services.AddScoped<UserContextAccessor>();
    builder.Services.AddTransient<ApiAuthenticationHandler>();

    builder.Services
        .AddHttpClient<TigerCsApiClient>((serviceProvider, client) =>
        {
            var options = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;

            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
            }

            client.Timeout = options.Timeout;
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        })
        .AddHttpMessageHandler<ApiAuthenticationHandler>();

    builder.Services.AddScoped<AuthApiClient>();
    builder.Services.AddScoped<UsersApiClient>();
    builder.Services.AddScoped<TicketsApiClient>();
    builder.Services.AddScoped<TicketSlaApiClient>();
    builder.Services.AddScoped<IntakeApiClient>();
    builder.Services.AddScoped<CrmApiClient>();
    builder.Services.AddScoped<VerificationSessionsApiClient>();
    builder.Services.AddScoped<DirectoryService>();
    builder.Services.AddScoped<QueueSlaResolver>();
    builder.Services.AddScoped<TigerCS.Web.Pages.Tickets.New.WizardStateStore>();

    var app = builder.Build();

    // ---------------------------------------------------------------------------
    // Pipeline
    // ---------------------------------------------------------------------------
    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    app.UseStatusCodePagesWithReExecute("/Error", "?status={0}");
    app.UseHttpsRedirection();
    app.UseStaticFiles();

    // Conservative response headers. No CSP nonce plumbing is needed because this
    // application ships no inline script and no external origin: every script and
    // stylesheet is a same-origin static file, so 'self' is the whole allow-list.
    app.Use(async (context, next) =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "same-origin";
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; "
            + "form-action 'self'; frame-ancestors 'none'; base-uri 'self'";
        await next();
    });

    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapRazorPages();
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

    app.Run();
    }
}
