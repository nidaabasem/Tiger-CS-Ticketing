using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// Keeps requirement 6 of the authorization correction ("every currently
/// protected API endpoint/action") honest over time.
///
/// <para>
/// <c>SystemAdministratorEndpointAuthorizationTests</c> proves the override
/// endpoint by endpoint, but a hand-written list of endpoints silently stops
/// meaning "every" the moment someone adds a controller action. This test
/// reads the host's real <see cref="EndpointDataSource"/> — the routes
/// ASP.NET Core actually mapped — and fails if the protected surface differs
/// from the inventory below. A new endpoint is then a failing test with a
/// message naming it, not an untested authorization gap.
/// </para>
/// </summary>
public class ProtectedEndpointInventoryTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public ProtectedEndpointInventoryTests(TigerCsApiFactory factory) => _factory = factory;

    /// <summary>
    /// Every protected endpoint, each with the test that proves a System
    /// Administrator JWT is authorized for it. Anonymous endpoints
    /// (<c>GET /health</c>, <c>POST /api/auth/login</c>) are excluded by the
    /// <c>[AllowAnonymous]</c> filter below, not by this list.
    /// </summary>
    private static readonly Dictionary<string, string> CoveredByTest = new()
    {
        ["POST /api/auth/logout"] = nameof(SystemAdministratorEndpointAuthorizationTests.Logout_Returns204),
        ["GET /api/users/me"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetOwnProfile_Returns200),
        ["PATCH /api/users/{employeeId:guid}/activation"] = nameof(SystemAdministratorEndpointAuthorizationTests.SetUserActivation_Returns200),
        ["GET /api/roles"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetRoleCatalog_Returns200),
        ["GET /api/departments"] = nameof(SystemAdministratorEndpointAuthorizationTests.ListDepartments_Returns200),
        ["GET /api/departments/{departmentId:int}/users"] = nameof(SystemAdministratorEndpointAuthorizationTests.ListDepartmentUsers_Returns200),
        ["GET /api/categories"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetCategories_Returns200),
        ["GET /api/crm/units/{crmUnitId}"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetCrmUnit_Returns200),
        ["GET /api/crm/units/search"] = nameof(SystemAdministratorEndpointAuthorizationTests.SearchCrmUnits_Returns200),
        ["GET /api/crm/units/{crmUnitId}/contacts"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetCrmUnitContacts_Returns200),
        ["GET /api/crm/buyers"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetBuyerByPhone_Returns200),
        ["POST /api/verification-sessions"] = nameof(SystemAdministratorEndpointAuthorizationTests.CreateVerificationSession_Returns201),
        ["GET /api/verification-sessions/{verificationSessionId:guid}"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetVerificationSession_Returns200),
        ["POST /api/intake-records"] = nameof(SystemAdministratorEndpointAuthorizationTests.CreateIntakeRecord_WithSystemAdministratorJwt_Returns201),
        ["GET /api/intake-records/{intakeRecordId:long}/customer-lookup"] = nameof(SystemAdministratorEndpointAuthorizationTests.SearchCustomerLookup_Returns200),
        ["POST /api/tickets"] = nameof(SystemAdministratorEndpointAuthorizationTests.CreateTicket_WithCustomerMatch_Returns201),
        ["GET /api/tickets"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketQueue_Returns200),
        ["GET /api/tickets/{ticketId:long}"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketDetail_ForATicketInADepartmentTheAdministratorDoesNotBelongTo_Returns200),
        ["POST /api/tickets/{ticketId:long}/assignment"] = nameof(SystemAdministratorEndpointAuthorizationTests.AssignTicket_Returns200),
        ["POST /api/tickets/{ticketId:long}/transfer"] = nameof(SystemAdministratorEndpointAuthorizationTests.TransferTicket_Returns200),
        ["POST /api/tickets/{ticketId:long}/status"] = nameof(SystemAdministratorEndpointAuthorizationTests.ChangeTicketStatus_Returns200),
        ["POST /api/tickets/{ticketId:long}/resolution"] = nameof(SystemAdministratorEndpointAuthorizationTests.ResolveAndCloseTicket_BothReturn200),
        ["POST /api/tickets/{ticketId:long}/close"] = nameof(SystemAdministratorEndpointAuthorizationTests.ResolveAndCloseTicket_BothReturn200),
        ["POST /api/tickets/{ticketId:long}/reconciliation"] = nameof(SystemAdministratorEndpointAuthorizationTests.ReconcileUnverifiedTicket_Returns200),
        ["POST /api/tickets/{ticketId:long}/notes"] = nameof(SystemAdministratorEndpointAuthorizationTests.AddAndListTicketNotes_Return201And200),
        ["GET /api/tickets/{ticketId:long}/notes"] = nameof(SystemAdministratorEndpointAuthorizationTests.AddAndListTicketNotes_Return201And200),
        ["GET /api/tickets/{ticketId:long}/customer-history"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketCustomerHistory_Returns200),
        ["GET /api/tickets/{ticketId:long}/customer-profile"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketCustomerProfile_Returns200),

        ["GET /api/customers/crm/{crmCustomerId:int}/ticket-history"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetCrmCustomerTicketHistory_Returns200),

        // SLA and Escalation. Automatic Level 2 escalation on breach has no
        // row here because it has no endpoint: MVP-API-Contracts.md §5.7
        // makes it system-triggered, raised inside the background-job
        // transaction that records the breach.
        ["GET /api/tickets/{ticketId:long}/sla"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketSla_Returns200),
        ["POST /api/tickets/{ticketId:long}/sla/first-response"] = nameof(SystemAdministratorEndpointAuthorizationTests.RecordFirstResponse_Returns200),
        ["POST /api/tickets/{ticketId:long}/escalations"] = nameof(SystemAdministratorEndpointAuthorizationTests.EscalateTicketAndListEscalations_Return201And200),
        ["GET /api/tickets/{ticketId:long}/escalations"] = nameof(SystemAdministratorEndpointAuthorizationTests.EscalateTicketAndListEscalations_Return201And200)
    };

    private static IReadOnlyCollection<string> ProtectedEndpointsOf(EndpointDataSource endpoints) =>
        endpoints.Endpoints
            .OfType<RouteEndpoint>()
            // Anonymous by explicit opt-out: GET /health and POST
            // /api/auth/login, the only two Security-Architecture.md §5
            // allows.
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .SelectMany(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
                return methods.Select(m => $"{m} /{e.RoutePattern.RawText?.TrimStart('/')}");
            })
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void EveryProtectedEndpointHasASystemAdministratorAuthorizationTest()
    {
        using var scope = _factory.Services.CreateScope();
        var endpoints = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();

        var actual = ProtectedEndpointsOf(endpoints);

        var untested = actual.Where(e => !CoveredByTest.ContainsKey(e)).ToList();
        Assert.True(
            untested.Count == 0,
            "These protected endpoints have no System Administrator authorization test (requirement 6). "
            + "Add one to SystemAdministratorEndpointAuthorizationTests and list it in CoveredByTest: "
            + string.Join(", ", untested));

        var stale = CoveredByTest.Keys.Where(e => !actual.Contains(e)).ToList();
        Assert.True(
            stale.Count == 0,
            "These entries in CoveredByTest no longer match a mapped endpoint — the route changed or was removed: "
            + string.Join(", ", stale));
    }

    [Fact]
    public void TheOnlyAnonymousBusinessEndpointsAreHealthAndLogin()
    {
        // Security-Architecture.md §5: "no anonymous endpoint exists except
        // the health-check surface" (plus login itself, which cannot require
        // a token it issues). Pinned here because the override's safety
        // argument assumes every other endpoint sits behind the authenticated
        // fallback policy — an endpoint that opted out of authorization
        // entirely would not be covered by an authorization override at all.
        //
        // The two OpenAPI document routes are anonymous by design and are
        // not business endpoints: they are mapped only in
        // OpenApiDocumentation.EnabledEnvironments (Development/Testing —
        // this factory runs as "Testing") and never exist in Production, per
        // MapTigerCsSwagger. Listed explicitly rather than filtered out by a
        // pattern, so a third anonymous route would still fail this test.
        using var scope = _factory.Services.CreateScope();
        var endpoints = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();

        var anonymous = endpoints.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => $"/{e.RoutePattern.RawText?.TrimStart('/')}")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "/api/auth/login",
                "/health",
                "/openapi/{documentName}.json",
                "/swagger/{documentName}/swagger.json"
            ],
            anonymous);
    }
}
