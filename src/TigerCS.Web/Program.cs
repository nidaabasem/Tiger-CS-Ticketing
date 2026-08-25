using Microsoft.AspNetCore.Authentication.Cookies;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages(options =>
{
    // Every page requires sign-in by default; only Login/Index/AccessDenied
    // (and the anonymous health check below) opt out.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/AccessDenied");
});
builder.Services.AddHttpContextAccessor();

builder.Services.Configure<TigerCsApiOptions>(builder.Configuration.GetSection(TigerCsApiOptions.SectionName));
var apiBaseUrl = builder.Configuration.GetSection(TigerCsApiOptions.SectionName)["BaseUrl"]
    ?? throw new InvalidOperationException("TigerCsApi:BaseUrl is not configured.");

// Bridges the Web app's own session to TigerCS.Api: the browser only ever
// sees this app's encrypted, HttpOnly cookie (below); the Api's JWT lives
// server-side inside that cookie's claims and is attached to every
// outgoing call here, never sent to the client as readable data.
builder.Services.AddTransient<BearerTokenHandler>();
builder.Services.AddScoped<TicketNameResolver>();

// AuthApiClient signs in/out — no bearer token to attach yet.
builder.Services.AddHttpClient<AuthApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl));

// Every other client calls authenticated endpoints.
builder.Services.AddHttpClient<TicketsApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<TicketSlaApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<UsersApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IntakeRecordsApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<CustomerLookupApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<CategoriesApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        // Standard ASP.NET Core cookie authentication: encrypted via Data
        // Protection and HttpOnly by default. No prior cookie-auth pattern
        // exists anywhere in this solution (confirmed before writing this),
        // so this is the platform's own mechanism, not a custom scheme.
        options.Cookie.Name = "TigerCS.Web.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
        // Sliding is off: TigerCS.Api's JWT has a fixed lifetime and no
        // refresh endpoint, so the cookie's own expiry (set per sign-in to
        // the token's exact ExpiresAtUtc) must not silently outlive it.
        options.SlidingExpiration = false;
    });

builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Strict CSP: same-origin only, no inline scripts/styles, no framing.
app.Use(async (context, next) =>
{
    context.Response.Headers.Append(
        "Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'none'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'");
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
