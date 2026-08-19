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
        var connectionString = configuration.GetConnectionString("TigerCsDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'TigerCsDatabase' is not configured. See docs/DEV-SETUP.md.");

        services.AddDbContext<TigerCsDbContext>(options => options.UseSqlServer(connectionString));

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                // Security-Architecture.md §1: [ASSUMPTION] — exact policy not yet
                // specified by management; these are the concrete values chosen for
                // that placeholder, one notch above Identity's own bare defaults.
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequiredUniqueChars = 4;

                // Security-Architecture.md §13.
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                options.User.RequireUniqueEmail = false;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<TigerCsDbContext>()
            .AddSignInManager();

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
                Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo, Roles.SystemAdministrator
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
