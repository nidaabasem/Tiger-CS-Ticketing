using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

/// <summary>
/// Marks an endpoint (or controller) a configured service identity may call
/// although it is not behind the <c>CustomerVerification</c> policy: the
/// Genesys Collections routes, <c>api/users/me</c> and the account endpoints
/// (logout / change-password).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AllowServiceIdentityAttribute : Attribute;

/// <summary>
/// Marks a write endpoint that a read-only caller (Chairman/CEO, Reporting
/// User) may still call, because management documented the permission: the
/// Chairman/CEO may <i>request</i> a Reopen Approval (Solution-Analysis.md
/// section 4.1), and every user may use the account endpoints.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AllowReadOnlyCallerWriteAttribute : Attribute;

/// <summary>Reads the configured service identities: <see cref="ServiceIdentityOptions"/> plus the Collections integration accounts.</summary>
public sealed class ServiceIdentityRegistry(
    IOptions<ServiceIdentityOptions> options,
    IOptions<CollectionsOptions> collectionsOptions) : IServiceIdentityRegistry
{
    public IReadOnlyCollection<Guid> ServiceIdentityIds =>
        [.. options.Value.EmployeeIds.Concat(collectionsOptions.Value.Authorization.IntegrationEmployeeIds).Distinct()];

    public bool IsServiceIdentity(Guid employeeId) => ServiceIdentityIds.Contains(employeeId);
}

/// <summary>
/// Added to every policy. A configured service identity (the Genesys
/// integration account) is a CS Agent by permission set but not a person: it
/// may reach only the integration surface - endpoints behind the
/// <c>CustomerVerification</c> policy (<c>api/genesys/*</c>,
/// <c>api/verification-sessions</c>, <c>api/crm/*</c>, ticket create,
/// reconciliation, intake records, customer lookup) or marked
/// <see cref="AllowServiceIdentityAttribute"/>. Everything else - admin,
/// reports, dashboard, user lists, ticket reads, close/reopen/resolve/assign/
/// transfer, notes, approvals, pending interactions - answers 403.
///
/// <para>
/// It restricts, never grants: a non-service caller always passes. The
/// System Administrator override still succeeds this requirement (it is not
/// an identity gate), but an administrator account is not a service identity.
/// </para>
/// </summary>
public sealed class ServiceIdentityRestrictionRequirement : IAuthorizationRequirement;

public sealed class ServiceIdentityRestrictionHandler(IServiceIdentityRegistry registry)
    : AuthorizationHandler<ServiceIdentityRestrictionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ServiceIdentityRestrictionRequirement requirement)
    {
        var idValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is null || !Guid.TryParse(idValue, out var employeeId) || !registry.IsServiceIdentity(employeeId))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var endpoint = (context.Resource as HttpContext)?.GetEndpoint();
        if (endpoint is null)
        {
            // No endpoint to inspect (a direct policy evaluation): nothing to restrict.
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var allowed = endpoint.Metadata.GetMetadata<AllowServiceIdentityAttribute>() is not null
            || endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(a => string.Equals(a.Policy, PolicyNames.CustomerVerification, StringComparison.Ordinal));
        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Added to every policy. A caller who holds only read-only roles
/// (<see cref="Roles.IsReadOnlyCaller"/> - Chairman/CEO, Reporting User) may
/// not use any mutating HTTP method, except on endpoints marked
/// <see cref="AllowReadOnlyCallerWriteAttribute"/>. This is the single
/// server-side backstop; role sets and application services also refuse the
/// individual actions.
/// </summary>
public sealed class ReadOnlyCallerWriteGuardRequirement : IAuthorizationRequirement;

public sealed class ReadOnlyCallerWriteGuardHandler : AuthorizationHandler<ReadOnlyCallerWriteGuardRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ReadOnlyCallerWriteGuardRequirement requirement)
    {
        if (context.Resource is not HttpContext http
            || HttpMethods.IsGet(http.Request.Method)
            || HttpMethods.IsHead(http.Request.Method)
            || HttpMethods.IsOptions(http.Request.Method))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var roles = context.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        if (!Roles.IsReadOnlyCaller(roles)
            || http.GetEndpoint()?.Metadata.GetMetadata<AllowReadOnlyCallerWriteAttribute>() is not null)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
