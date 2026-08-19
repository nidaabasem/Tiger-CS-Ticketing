using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using TigerCS.Infrastructure;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Seed;

// Never log token/claim contents (review item 4) — IdentityModelEventSource's PII
// logging defaults to off already, but this makes the choice explicit rather than
// relying on the library default silently staying that way across upgrades.
IdentityModelEventSource.ShowPII = false;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddTigerCsInfrastructure(builder.Configuration);

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    using var scope = app.Services.CreateScope();
    await DevSeedData.SeedAsync(scope.ServiceProvider);
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

// Phase 1 foundation placeholder (S-01), kept anonymous per
// Security-Architecture.md §5's explicit health-check exception.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.MapControllers();

app.Run();
