using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using TigerCS.Api.OpenApi;
using TigerCS.Infrastructure;
using TigerCS.Infrastructure.BackgroundJobs;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Seed;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Integrations.Modules.EmailIntegration;

// Never log token/claim contents (review item 4) — IdentityModelEventSource's PII
// logging defaults to off already, but this makes the choice explicit rather than
// relying on the library default silently staying that way across upgrades.
IdentityModelEventSource.ShowPII = false;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Every expected failure path in this Api already returns a structured
// ProblemDetails body (Problem(...)/ValidationProblem(...) throughout the
// controllers). Without this, an *unexpected* exception instead falls
// through to Kestrel's bare, empty-body 500 — which TigerCS.Web's
// ApiClientBase.DescribeFailureAsync cannot parse for a "detail", so the
// Web page silently shows its own generic fallback text ("Unable to load
// the department list.", "Could not record this interaction.") with no
// indication anything actually went wrong server-side. This maps every
// unhandled exception to a real ProblemDetails JSON body instead.
builder.Services.AddProblemDetails();

// Swagger/OpenAPI document generation (TigerCS.Api/OpenApi). Registering the
// generator is unconditional; whether /swagger and /swagger/v1/swagger.json
// are actually reachable is decided by MapTigerCsSwagger below, which maps
// nothing outside OpenApiDocumentation.EnabledEnvironments.
builder.Services.AddTigerCsOpenApi();

builder.Services.AddTigerCsInfrastructure(builder.Configuration);
builder.Services.AddTigerCsIntegrations(builder.Configuration);

// Bound lazily from JwtOptions (IOptions<JwtOptions>, resolved at first use) rather
// than read from builder.Configuration inline here — the latter would run before
// test/host configuration overrides (e.g. WebApplicationFactory) are merged in.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>, IHostEnvironment>((bearerOptions, jwtOptions, env) =>
    {
        var jwt = jwtOptions.Value;

        // RequireHttpsMetadata governs fetching OIDC metadata over HTTP vs HTTPS
        // (moot here — no Authority/metadata address is configured, so no
        // metadata endpoint is ever fetched) but is set explicitly per
        // environment anyway, per review item 4, rather than left at whatever
        // the library's own default happens to be.
        bearerOptions.RequireHttpsMetadata = !env.IsDevelopment();

        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization(options => options.AddTigerCsAuthorizationPolicies());

var app = builder.Build();

// No production deployment is authorized at this pilot stage — enforced
// through release governance and documentation (docs/DEV-SETUP.md,
// docs/architecture/adr/0022-deployment-strategy.md), not by refusing to
// start here. An unconditional IsProduction() throw would make this
// application unable to ever run in Production even once that's actually
// authorized, which isn't this code's decision to make.

// Fail fast for the JWT signing key specifically: validate it
// exists and meets the minimum length for HS256 (256 bits / 32 bytes) before
// the app starts accepting traffic, instead of only discovering a missing or
// weak key on the first authenticated request.
using (var startupScope = app.Services.CreateScope())
{
    var jwtOptions = startupScope.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;
    if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey))
    {
        throw new InvalidOperationException("Jwt:SigningKey is not configured. See docs/DEV-SETUP.md.");
    }

    if (Encoding.UTF8.GetByteCount(jwtOptions.SigningKey) < 32)
    {
        throw new InvalidOperationException(
            "Jwt:SigningKey must be at least 32 bytes (256 bits) for HMAC-SHA256. See docs/DEV-SETUP.md.");
    }

    if (string.IsNullOrWhiteSpace(jwtOptions.Issuer) || string.IsNullOrWhiteSpace(jwtOptions.Audience))
    {
        throw new InvalidOperationException("Jwt:Issuer and Jwt:Audience must both be configured. See docs/DEV-SETUP.md.");
    }
}

// Fail fast if the mock CRM adapter would run outside Development/Testing.
// The actual decision is CrmGatewaySafety.IsUnsafe — conditional on the
// selected gateway type (Crm:Provider), not on the environment name alone;
// see that class's own remarks. A real ICrmGateway implementation
// (Crm:Provider set to anything other than "Mock",
// IntegrationsServiceCollectionExtensions) is judged safe here in every
// environment, Production included.
using (var crmStartupScope = app.Services.CreateScope())
{
    var crmOptions = crmStartupScope.ServiceProvider.GetRequiredService<IOptions<CrmGatewayOptions>>().Value;
    if (CrmGatewaySafety.IsUnsafe(crmOptions.Provider, app.Environment.EnvironmentName))
    {
        throw new InvalidOperationException(
            $"Crm:Provider is 'Mock' in environment '{app.Environment.EnvironmentName}'. MockCrmGateway is " +
            "never production-ready (see its own remarks) and may only run in " +
            $"{string.Join("/", CrmGatewaySafety.MockAllowedEnvironments)}. Configure a real ICrmGateway " +
            "implementation and set Crm:Provider accordingly before deploying to this environment.");
    }
}

// Fail fast if the recording email adapter would run outside
// Development/Testing. Same conditional-on-provider shape as the CRM guard
// above (EmailSenderSafety.IsUnsafe), and for a sharper reason:
// RecordingEmailSender reports every send as successful without contacting
// any provider, so running it for real would mark tickets acknowledged, write
// Sent notification rows and satisfy every dashboard and audit query while no
// customer ever received anything. A silent, total failure that looks exactly
// like success is worse than a loud one.
using (var emailStartupScope = app.Services.CreateScope())
{
    var emailOptions = emailStartupScope.ServiceProvider.GetRequiredService<IOptions<EmailSenderOptions>>().Value;
    if (EmailSenderSafety.IsUnsafe(emailOptions.Provider, app.Environment.EnvironmentName))
    {
        throw new InvalidOperationException(
            $"Notifications:Email:Provider is 'Recording' in environment '{app.Environment.EnvironmentName}'. "
            + "RecordingEmailSender never delivers anything (see its own remarks) and may only run in "
            + $"{string.Join("/", EmailSenderSafety.RecordingAllowedEnvironments)}. No real email provider is "
            + "confirmed for this pilot: configure a real IEmailSender implementation and set "
            + "Notifications:Email:Provider accordingly before deploying to this environment.");
    }
}

// Swagger UI at /swagger and the OpenAPI JSON at /swagger/v1/swagger.json,
// in Development and Testing only — never mapped in Production (see
// OpenApiDocumentation.EnabledEnvironments).
app.MapTigerCsSwagger();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await DevSeedData.SeedAsync(scope.ServiceProvider);
}

// SLA-Architecture.md §14 / ADR-0015 — registers the recurring safety sweep
// with Hangfire. A no-op when BackgroundJobs:Enabled is false; registered
// after Build() because the recurring-job manager needs live job storage.
// The per-deadline scheduled jobs of §13 need no registration here: they are
// enqueued as each due timestamp is computed.
using (var backgroundJobScope = app.Services.CreateScope())
{
    var backgroundJobOptions = backgroundJobScope.ServiceProvider
        .GetRequiredService<IOptions<BackgroundJobOptions>>().Value;
    app.Services.UseTigerCsRecurringSlaSweep(backgroundJobOptions);

    // ADR-0013/ADR-0015 — the recurring Outbox dispatcher. Registered
    // alongside the sweep because both need live job storage. With
    // BackgroundJobs:Enabled false this is a no-op and Outbox rows simply
    // stay Pending: the switch governs when delivery happens, never whether
    // the intent to deliver was durably recorded.
    var outboxOptions = backgroundJobScope.ServiceProvider
        .GetRequiredService<IOptions<OutboxDispatchOptions>>().Value;
    app.Services.UseTigerCsRecurringOutboxDispatch(backgroundJobOptions, outboxOptions);
}

app.UseExceptionHandler();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

// Phase 1 foundation placeholder (S-01), kept anonymous per
// Security-Architecture.md §5's explicit health-check exception.
app.MapGet("/health", () => Results.Ok(new HealthResponse("healthy")))
    .AllowAnonymous()
    .WithTags(OpenApiTags.Health)
    .WithName("GetHealth")
    .WithSummary("Liveness probe.")
    .WithDescription(
        "Returns 200 with a constant payload while the application is running. The only endpoint "
        + "that does not require authentication apart from POST /api/auth/login.")
    .Produces<HealthResponse>(StatusCodes.Status200OK)
    // Minimal-API endpoints carry no XML doc comments, so the 200's
    // description would otherwise be the generic reason phrase.
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Responses!["200"].Description = "The application is running.";
        return Task.CompletedTask;
    });

app.MapControllers();

app.Run();

/// <summary>The <c>GET /health</c> payload.</summary>
/// <remarks>A named type rather than an anonymous object purely so the response has a schema in the OpenAPI document; the JSON it serialises to is unchanged.</remarks>
/// <param name="Status">Constant literal <c>"healthy"</c>.</param>
public sealed record HealthResponse(string Status);
