using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.ClassificationAndRouting.Services;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Audit;
using TigerCS.Infrastructure.BackgroundJobs;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.CustomerVerification.Repositories;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;
using TigerCS.Infrastructure.Modules.Notifications.Repositories;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
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

        // The central System Administrator authorization override (ADR-0024).
        // Registered once, here, rather than referenced by any policy or
        // controller: ASP.NET Core runs every registered IAuthorizationHandler
        // against every authorization evaluation, so this one registration is
        // what makes the override apply to the whole policy catalog — and to
        // policies added after it. Singleton because the handler holds no
        // state and touches no scoped service; the other two are scoped
        // because they inject scoped repositories.
        services.AddSingleton<IAuthorizationHandler, SystemAdministratorOverrideHandler>();

        services.AddScoped<AuthenticationAppService>();
        services.AddScoped<UserProfileAppService>();
        services.AddScoped<DepartmentUserAppService>();
        services.AddScoped<DepartmentDirectoryAppService>();
        services.AddScoped<RoleCatalogAppService>();
        services.AddScoped<UserActivationAppService>();
        services.AddScoped<DepartmentAssignmentService>();

        services.AddScoped<IAuditEntryWriter, AuditEntryWriter>();

        services.AddScoped<IUnitReferenceRepository, UnitReferenceRepository>();
        services.AddScoped<IContactReferenceRepository, ContactReferenceRepository>();
        services.AddScoped<IVerificationSessionRepository, VerificationSessionRepository>();
        services.AddScoped<ICustomerVerificationUnitOfWork, CustomerVerificationUnitOfWork>();
        services.AddScoped<CrmUnitLookupAppService>();
        services.AddScoped<CrmBuyerLookupAppService>();
        services.AddScoped<VerificationSessionAppService>();

        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<CategoryCatalogAppService>();
        services.AddScoped<IPriorityRepository, PriorityRepository>();
        services.AddScoped<IIntakeRecordRepository, IntakeRecordRepository>();
        services.AddScoped<IDepartmentCustomerLookupSourceRepository, DepartmentCustomerLookupSourceRepository>();
        services.AddScoped<ITicketRepository, TicketRepository>();
        services.AddScoped<ITicketRequesterSnapshotRepository, TicketRequesterSnapshotRepository>();
        services.AddScoped<ITicketStatusHistoryRepository, TicketStatusHistoryRepository>();
        services.AddScoped<ITicketAssignmentRepository, TicketAssignmentRepository>();
        services.AddScoped<ITicketResolutionRepository, TicketResolutionRepository>();
        services.AddScoped<ITicketNoteRepository, TicketNoteRepository>();
        services.AddScoped<ITicketingUnitOfWork, TicketingUnitOfWork>();
        services.AddScoped<IntakeRecordAppService>();
        services.AddScoped<CustomerLookupAppService>();
        services.AddScoped<TicketCreationAppService>();
        services.AddScoped<TicketQueryAppService>();
        services.AddScoped<TicketAssignmentAppService>();
        services.AddScoped<TicketLifecycleAppService>();
        services.AddScoped<TicketNoteAppService>();
        services.AddScoped<TicketReconciliationAppService>();
        services.AddScoped<CustomerHistoryAppService>();

        // SLA and Escalation (ADR-0009/0010/0011/0014/0015). No policy,
        // controller or role list here names the System Administrator role:
        // every service below routes its resource-scoped decisions through
        // AuthorizationGate and every policy through
        // SystemAdministratorOverrideHandler, so ADR-0024's central override
        // covers this whole module without a line of override-specific code
        // — which is the structural property requirement 4 of that
        // correction asked for.
        services.AddScoped<ISlaPolicyRepository, SlaPolicyRepository>();
        services.AddScoped<IBusinessCalendarRepository, BusinessCalendarRepository>();
        services.AddScoped<ITicketSlaInstanceRepository, TicketSlaInstanceRepository>();
        services.AddScoped<ITicketEscalationRepository, TicketEscalationRepository>();
        services.AddScoped<IIdempotencyRecordStore, IdempotencyRecordStore>();
        services.AddScoped<SlaDueDateService>();
        services.AddScoped<SlaBreachProcessor>();
        services.AddScoped<SlaBreachDetectionAppService>();
        services.AddScoped<SlaQueryAppService>();
        services.AddScoped<SlaFirstResponseAppService>();
        services.AddScoped<TicketEscalationAppService>();

        // Notifications and the transactional Outbox (ADR-0013/ADR-0014,
        // MVP-Data-Dictionary.md §2.21/§2.23).
        //
        // IOutboxWriter is registered here, in the Infrastructure module that
        // Module-Design.md says owns OutboxMessage — Ticketing consumes the
        // port and never references the Notifications module, so the
        // prohibited-dependency rule holds in both directions.
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddScoped<IOutboxMessageRepository, OutboxMessageRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<INotificationsUnitOfWork, NotificationsUnitOfWork>();

        // Every IOutboxEventHandler registered against this interface is
        // discovered by the dispatcher and matched on its own EventType, so a
        // later consumer (SLA breach alerts, Genesys event processing) is one
        // registration with no dispatcher change.
        services.AddScoped<IOutboxEventHandler, TicketAcknowledgementHandler>();

        services.AddScoped(sp => sp
            .GetRequiredService<IOptions<OutboxDispatchOptions>>().Value.ToPolicy());
        services.AddScoped<OutboxDispatcher>();

        services.AddTigerCsBackgroundJobs(configuration);

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

        // CS Agent/CS Supervisor only — see PolicyNames.CustomerVerification's own
        // remarks for the Solution-Analysis.md §4.1 vs. MVP-API-Contracts.md
        // §0 conflict this resolves, confirmed rather than guessed.
        options.AddPolicy(PolicyNames.CustomerVerification, Base()
            .RequireRole(Roles.CsAgent, Roles.CsSupervisor)
            .Build());

        return options;
    }
}
