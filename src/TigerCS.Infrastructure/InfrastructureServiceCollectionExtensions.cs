using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddTigerCsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Resolved lazily (IConfiguration from DI, at first DbContext creation)
        // rather than read from the `configuration` parameter eagerly here —
        // an eager read would run before test-host configuration overrides
        // (e.g. WebApplicationFactory) are merged in, same reasoning as the
        // JWT options below.
        services.AddDbContext<TigerCsDbContext>((serviceProvider, options) =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("TigerCsDatabase")
                ?? throw new InvalidOperationException(
                    "Connection string 'TigerCsDatabase' is not configured. See docs/DEV-SETUP.md.");
            options.UseSqlServer(connectionString);
        });

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                // Security-Architecture.md §1/§13: [ASSUMPTION] — exact policy not
                // yet specified by management. These are the pilot defaults, one
                // notch above Identity's own bare defaults — NOT hardcoded final
                // values: the Configure<IdentityOptions> call below layers any
                // "Identity:Password:*"/"Identity:Lockout:*" configuration on top,
                // so a real deployment can override them without a code change.
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequiredUniqueChars = 4;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                options.User.RequireUniqueEmail = false;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<TigerCsDbContext>()
            .AddSignInManager();

        // Optional override of the pilot defaults above via configuration
        // ("Identity:Password:RequiredLength", "Identity:Lockout:MaxFailedAccessAttempts",
        // etc.) — only keys actually present in configuration change anything;
        // this and the validation below run on Configure<IdentityOptions>'s IOptions
        // pipeline, so it applies to every options access, not only at startup.
        services.Configure<IdentityOptions>(configuration.GetSection("Identity"));

        services.AddOptions<IdentityOptions>()
            .Validate(o => o.Password.RequiredLength >= 8,
                "Identity:Password:RequiredLength must be at least 8 (Security-Architecture.md §1 pilot floor).")
            .Validate(o => o.Lockout.MaxFailedAccessAttempts is > 0 and <= 10,
                "Identity:Lockout:MaxFailedAccessAttempts must be between 1 and 10.")
            .Validate(o => o.Lockout.DefaultLockoutTimeSpan >= TimeSpan.FromMinutes(1),
                "Identity:Lockout:DefaultLockoutTimeSpan must be at least 1 minute.")
            .ValidateOnStart();

        services.AddHttpContextAccessor();
        services.AddScoped<IClaimsTransformation, DepartmentClaimsTransformation>();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IEmployeeRepository, EmployeeRepository>();
        services.AddScoped<IDepartmentRepository, DepartmentRepository>();
        services.AddScoped<IUserDepartmentAssignmentRepository, UserDepartmentAssignmentRepository>();
        services.AddScoped<IIdentityUnitOfWork, IdentityUnitOfWork>();
        services.AddScoped<IUserRoleReader, UserRoleReader>();
        services.AddScoped<IRoleCatalogReader, RoleCatalogReader>();
        services.AddScoped<IIdentityAuthenticator, IdentityAuthenticator>();
        services.AddScoped<ITokenService, JwtTokenService>();
        services.AddScoped<IEmployeeDirectory, EmployeeDirectory>();
        services.AddScoped<ICurrentUserContext, CurrentUserContext>();

        services.AddScoped<IAuthorizationHandler, ActiveEmployeeHandler>();
        services.AddScoped<IAuthorizationHandler, DepartmentScopedHandler>();

        services.AddScoped<AuthenticationAppService>();
        services.AddScoped<UserProfileAppService>();
        services.AddScoped<DepartmentUserAppService>();
        services.AddScoped<RoleCatalogAppService>();
        services.AddScoped<UserActivationAppService>();
        services.AddScoped<DepartmentAssignmentService>();

        return services;
    }

    public static AuthorizationOptions AddTigerCsAuthorizationPolicies(this AuthorizationOptions options)
    {
        AuthorizationPolicyBuilder Base() =>
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new ActiveEmployeeRequirement());

        options.DefaultPolicy = Base().Build();

        // Security-Architecture.md §5: "no anonymous endpoint exists except
        // the health-check surface" — every endpoint requires authentication
        // unless it explicitly opts out with [AllowAnonymous].
        options.FallbackPolicy = Base().Build();

        options.AddPolicy(PolicyNames.AuthenticatedStaff, Base().Build());

        options.AddPolicy(PolicyNames.DepartmentScoped, Base()
            .AddRequirements(new DepartmentScopedRequirement(
            [
                // Security-Architecture.md §3, verbatim: "CS-layer roles (Geyness
                // Agent, Supervisor, CS Manager) are scoped differently — across
                // all departments." These three (CS Agent/CS Supervisor/CS
                // Manager per this increment's role-naming decision) are the only
                // roles that citation names as cross-department. General
                // Manager/Chairman-CEO/System Administrator are included too,
                // on the separate basis of Solution-Analysis.md §4.1's
                // permission matrix, which gives each of them "View: All
                // tickets" — a broader read grant than §3's Close/Reopen-
                // specific carve-out. Flagged for confirmation before a real
                // ticket endpoint consumes this policy: whether GM/Chairman/
                // SysAdmin's cross-department reach should extend beyond View.
                Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager,
                Roles.GeneralManager, Roles.ChairmanCeo, Roles.SystemAdministrator
            ]))
            .Build());

        options.AddPolicy(PolicyNames.SupervisorOrAbove, Base()
            .RequireRole(Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo)
            .Build());

        options.AddPolicy(PolicyNames.DepartmentHeadOrAbove, Base()
            .RequireRole(Roles.DepartmentHead, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo)
            .Build());

        options.AddPolicy(PolicyNames.CsManagerOrGeneralManager, Base()
            .RequireRole(Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo)
            .Build());

        options.AddPolicy(PolicyNames.SystemAdministrator, Base()
            .RequireRole(Roles.SystemAdministrator)
            .Build());

        return options;
    }
}
