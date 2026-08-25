using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// Requirement 6 of the authorization correction (ADR-0024): proves a
/// System Administrator JWT is authorized for <b>every</b> currently
/// protected API endpoint/action, end-to-end against the real host — real
/// routing, real JWT validation, the real policy catalog and the real
/// application services.
///
/// <para>
/// <b>Authorized, not merely "not 403".</b> Each test asserts the endpoint's
/// own success status. A 403 would fail, and so would a 500 or a
/// business-rule rejection dressed up as success, which is what makes these
/// tests evidence for requirement 2 as well: the administrator reaches the
/// business logic and the business logic behaves normally.
/// </para>
///
/// <para>
/// The account created here holds <b>only</b> <c>Roles.SystemAdministrator</c>
/// — requirement 3. Every authorization it passes below therefore comes from
/// the central override, not from a second role quietly granted to make a
/// test pass. <c>SeedEmployeeAsync</c> assigns exactly one role, and
/// <see cref="AdministratorHoldsOnlyTheSystemAdministratorRole"/> asserts it
/// against the live token.
/// </para>
/// </summary>
public class SystemAdministratorEndpointAuthorizationTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public SystemAdministratorEndpointAuthorizationTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid EmployeeId)> CreateClientAsync(string role)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return (client, employeeId);
    }

    private Task<(HttpClient Client, Guid EmployeeId)> CreateAdministratorAsync() =>
        CreateClientAsync(Roles.SystemAdministrator);

    /// <summary>Runs the real intake -> customer lookup -> ticket sequence as the given client, returning the created ticket.</summary>
    private async Task<TicketDetailDto> CreateVerifiedTicketAsync(HttpClient client, string departmentPrefix)
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync(
            departmentPrefix + " " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        // "+971500000001" matches MockCrmGateway's CRM-UNIT-1001/CRM-CONTACT-2002 fixture.
        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null));
        intakeResponse.EnsureSuccessStatusCode();
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        lookupResponse.EnsureSuccessStatusCode();
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        var crmMatch = lookup!.Sources.Single(s => s.Source == "Crm");

        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake.IntakeRecordId, crmMatch.UnitReferenceId, crmMatch.ContactReferenceId, categoryId, (byte)PriorityLevel.High, "AC unit not cooling"));
        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var created = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();

        var detailResponse = await client.GetAsync($"/api/tickets/{created!.TicketId}");
        detailResponse.EnsureSuccessStatusCode();
        return (await detailResponse.Content.ReadFromJsonAsync<TicketDetailDto>())!;
    }

    private static byte[] RowVersionOf(TicketDetailDto ticket) => Convert.FromBase64String(ticket.RowVersion);

    // ---------------------------------------------------------------
    // Requirement 3 — the account is only ever "System Administrator".
    // ---------------------------------------------------------------

    [Fact]
    public async Task AdministratorHoldsOnlyTheSystemAdministratorRole()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<CurrentUserResponseDto>();
        Assert.Equal([Roles.SystemAdministrator], profile!.Roles);
    }

    // ---------------------------------------------------------------
    // Identity and Access endpoints
    // ---------------------------------------------------------------

    [Fact]
    public async Task Logout_Returns204()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task GetOwnProfile_Returns200()
    {
        var (client, employeeId) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<CurrentUserResponseDto>();
        Assert.Equal(employeeId, profile!.EmployeeId);
    }

    [Fact]
    public async Task GetRoleCatalog_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/roles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SetUserActivation_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        // A different employee, not the administrator themselves: the
        // "cannot deactivate the last active System Administrator" rule
        // (UserActivationAppService) is a business rule the override does not
        // touch, and this test is about authorization.
        var (_, _, targetEmployeeId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);

        var response = await client.PatchAsJsonAsync(
            $"/api/users/{targetEmployeeId}/activation", new ActivationRequestDto(false, "Left the company"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListDepartmentUsers_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);

        var response = await client.GetAsync($"/api/departments/{departmentId}/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // CRM unit/contact lookup (CustomerVerification policy)
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCrmUnit_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/crm/units/CRM-UNIT-1001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SearchCrmUnits_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/crm/units/search?unitNumber=1204");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetCrmUnitContacts_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// CRM Buyer Lookup. <see cref="TigerCsApiFactory"/> swaps
    /// <c>ICrmBuyerLookupGateway</c> for a fixture-backed fake in this test
    /// host — there is no real CRM to call here, same reasoning as
    /// <c>MockCrmGateway</c> for the unit/contact lookups above.
    /// </summary>
    [Fact]
    public async Task GetBuyerByPhone_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/crm/buyers?phoneNumber=%2B971500000900");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // Verification sessions
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateVerificationSession_Returns201()
    {
        var (client, _) = await CreateAdministratorAsync();

        var unit = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001"))
            .Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts"))
            .Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();

        var response = await client.PostAsJsonAsync(
            "/api/verification-sessions",
            new CreateVerificationSessionRequestDto(unit!.UnitReferenceId, contacts![0].ContactReferenceId, true, "ManualAgentConfirmation"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task GetVerificationSession_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var unit = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001"))
            .Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts"))
            .Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();
        var created = await (await client.PostAsJsonAsync(
                "/api/verification-sessions",
                new CreateVerificationSessionRequestDto(unit!.UnitReferenceId, contacts![0].ContactReferenceId, true, "ManualAgentConfirmation")))
            .Content.ReadFromJsonAsync<VerificationSessionResponseDto>();

        var response = await client.GetAsync($"/api/verification-sessions/{created!.VerificationSessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // Intake records — requirement 8, called out explicitly.
    // ---------------------------------------------------------------

    /// <summary>
    /// Requirement 8, verbatim: <c>POST /api/intake-records</c> with a System
    /// Administrator JWT succeeds with 201. Before this correction the
    /// CustomerVerification policy denied the role outright (403); the
    /// central override is the only thing that changed.
    /// </summary>
    [Fact]
    public async Task CreateIntakeRecord_WithSystemAdministratorJwt_Returns201()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Authorized *and* functional: the record is really created, not a
        // 201 over an empty write.
        var intake = await response.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        Assert.True(intake!.IntakeRecordId > 0);
        Assert.Equal("Phone", intake.ChannelId);
        Assert.True(intake.IsUnitRelated);
    }

    // ---------------------------------------------------------------
    // Customer lookup
    // ---------------------------------------------------------------

    [Fact]
    public async Task SearchCustomerLookup_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lookup = await response.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        Assert.Equal(3, lookup!.Sources.Count);
    }

    // ---------------------------------------------------------------
    // Ticket creation
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateTicket_WithCustomerMatch_Returns201()
    {
        var (client, _) = await CreateAdministratorAsync();

        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        Assert.Equal("Verified", ticket.VerificationStatus);
        Assert.Equal("Open", ticket.TicketStatus);
    }

    [Fact]
    public async Task CreateTicket_NonUnitIntake_Returns201()
    {
        var (client, _) = await CreateAdministratorAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("General Inquiry", departmentId);

        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500009999", null, false, null, null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Medium, "General billing question"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // Ticket queries
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetTicketQueue_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/tickets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetTicketDetail_ForATicketInADepartmentTheAdministratorDoesNotBelongTo_Returns200()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateVerifiedTicketAsync(agentClient, "Facilities");
        var (adminClient, _) = await CreateAdministratorAsync();

        var response = await adminClient.GetAsync($"/api/tickets/{ticket.TicketId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // Assignment, transfer, status, resolution, closure
    // ---------------------------------------------------------------

    [Fact]
    public async Task AssignTicket_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        var (_, _, workerId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(workerId, ticket.CurrentDepartmentId);

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(workerId, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal(workerId, updated!.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task TransferTicket_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");
        var targetDepartmentId = await _factory.CreateDepartmentAsync("Legal " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/transfer",
            new TransferTicketRequestDto(targetDepartmentId, "Misrouted", RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal(targetDepartmentId, updated!.CurrentDepartmentId);
    }

    [Fact]
    public async Task ChangeTicketStatus_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        var (_, _, workerId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(workerId, ticket.CurrentDepartmentId);

        // Open -> InProgress requires an owner (Ticket.ChangeStatus) — a
        // business rule the override does not suspend, so the administrator
        // assigns first, exactly as any other authorized role would have to.
        var assigned = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(workerId, RowVersionOf(ticket))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", RowVersionOf(assigned!)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("InProgress", updated!.TicketStatus);
    }

    [Fact]
    public async Task ResolveAndCloseTicket_BothReturn200()
    {
        // Resolve is Department Employee/Head only and Close is CS-layer only
        // (ISSUE-022) — the administrator holds neither role and performs
        // both here purely through the override.
        var (client, adminEmployeeId) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        await _factory.AssignPrimaryDepartmentAsync(adminEmployeeId, ticket.CurrentDepartmentId);

        var assigned = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(adminEmployeeId, RowVersionOf(ticket))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var inProgress = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", RowVersionOf(assigned!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var resolveResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "Fixed the AC unit.", null, null, RowVersionOf(inProgress!)));
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        var resolved = await resolveResponse.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Resolved", resolved!.TicketStatus);

        var closeResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(RowVersionOf(resolved)));
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        var closed = await closeResponse.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Closed", closed!.TicketStatus);
    }

    // ---------------------------------------------------------------
    // Notes
    // ---------------------------------------------------------------

    [Fact]
    public async Task AddAndListTicketNotes_Return201And200()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateVerifiedTicketAsync(agentClient, "Facilities");
        var (adminClient, _) = await CreateAdministratorAsync();

        var addResponse = await adminClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/notes", new CreateNoteRequestDto("Checked the audit trail for this ticket."));
        Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);

        var listResponse = await adminClient.GetAsync($"/api/tickets/{ticket.TicketId}/notes");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
    }

    // ---------------------------------------------------------------
    // SLA and Escalation (MVP-API-Contracts.md §5.1/§5.2/§5.7/§5.9)
    //
    // The structural claim ADR-0024 makes is that a policy added later is
    // covered with no change to the override. This whole module is that
    // claim's first real test: not one line of SLA or escalation code names
    // the System Administrator role, and none of the role sets in
    // SlaRoleSets include it — every endpoint below is reached purely
    // through the central mechanism.
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetTicketSla_Returns200()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateVerifiedTicketAsync(agentClient, "Facilities");
        var (adminClient, _) = await CreateAdministratorAsync();

        var response = await adminClient.GetAsync($"/api/tickets/{ticket.TicketId}/sla");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sla = await response.Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();

        // Not merely "not 403": the summary is real, with the due dates
        // ticket creation computed for it.
        Assert.NotNull(sla!.FirstResponseDueAtUtc);
        Assert.NotNull(sla.ResolutionDueAtUtc);
        Assert.Equal("Running", sla.SlaState);
    }

    [Fact]
    public async Task RecordFirstResponse_Returns200()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateVerifiedTicketAsync(agentClient, "Facilities");
        var (adminClient, _) = await CreateAdministratorAsync();

        var response = await adminClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("Manual", null, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sla = await response.Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();
        Assert.NotNull(sla!.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task EscalateTicketAndListEscalations_Return201And200()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateVerifiedTicketAsync(agentClient, "Facilities");
        var (adminClient, _) = await CreateAdministratorAsync();

        // Level 4 specifically: MVP-ERD.md §2.17 restricts ManualLevel4 to a
        // CS Manager or GM actor, and the administrator holds neither role.
        // Passing here is the override working on the narrowest gate in the
        // module, not on its most permissive one.
        var escalateResponse = await adminClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(4, "ManualLevel4", "Executive attention requested.", RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.Created, escalateResponse.StatusCode);
        var escalation = await escalateResponse.Content.ReadFromJsonAsync<TicketEscalationResponseDto>();
        Assert.Equal(4, escalation!.Level);

        var listResponse = await adminClient.GetAsync($"/api/tickets/{ticket.TicketId}/escalations");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var escalations = await listResponse.Content.ReadFromJsonAsync<List<TicketEscalationResponseDto>>();
        Assert.Single(escalations!);
    }

    // ---------------------------------------------------------------
    // CRM reconciliation
    // ---------------------------------------------------------------

    [Fact]
    public async Task ReconcileUnverifiedTicket_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        // Created with no customer match — business-rule change: customer
        // lookup no longer gates creation, so this ticket starts Unverified.
        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500009999", null, true, "1204", (byte)PriorityLevel.Critical)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var unverified = await (await client.PostAsJsonAsync(
                "/api/tickets",
                new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Critical, "Flooding in lobby")))
            .Content.ReadFromJsonAsync<TicketResponseDto>();

        // The CRM comes back: the administrator verifies the same unit
        // ("1204", matching the intake's RawUnitNumberEntered) and reconciles.
        var unit = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001"))
            .Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts"))
            .Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();
        var session = await (await client.PostAsJsonAsync(
                "/api/verification-sessions",
                new CreateVerificationSessionRequestDto(unit!.UnitReferenceId, contacts![0].ContactReferenceId, true, "ManualAgentConfirmation")))
            .Content.ReadFromJsonAsync<VerificationSessionResponseDto>();

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{unverified!.TicketId}/reconciliation",
            new ReconcileTicketRequestDto(session!.VerificationSessionId, Convert.FromBase64String(unverified.RowVersion)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reconciled = await response.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Verified", reconciled!.VerificationStatus);
    }

    // ---------------------------------------------------------------
    // Requirement 2 — the override is authorization only.
    // ---------------------------------------------------------------

    [Fact]
    public async Task ClosedTicketImmutability_StillAppliesToTheAdministrator()
    {
        var (client, adminEmployeeId) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");
        await _factory.AssignPrimaryDepartmentAsync(adminEmployeeId, ticket.CurrentDepartmentId);

        var assigned = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(adminEmployeeId, RowVersionOf(ticket))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var inProgress = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", RowVersionOf(assigned!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var resolved = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/resolution",
                new ResolveTicketRequestDto("Resolved", "Done.", null, null, RowVersionOf(inProgress!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var closed = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(RowVersionOf(resolved!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var closedRowVersion = RowVersionOf(closed!);

        // 422, not 403 and not 200: the administrator is authorized and the
        // business rule refuses anyway.
        var reassign = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(adminEmployeeId, closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reassign.StatusCode);

        var statusChange = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, statusChange.StatusCode);

        var reResolve = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "n/a", null, null, closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reResolve.StatusCode);

        var reClose = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reClose.StatusCode);
    }

    [Fact]
    public async Task InvalidStatusTransition_StillRejectedForTheAdministrator()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        // Open -> PendingCustomer is not in the transition table, and an
        // unassigned Open ticket cannot go InProgress either.
        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("PendingCustomer", RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RequestValidation_StillAppliesToTheAdministrator()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/crm/units/search?unitNumber=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Optimistic-concurrency control under the override is proven at the
    // application-service level instead, in
    // TicketAssignmentAppServiceTests.AssignAsync_SystemAdministrator_ConcurrentModification_*:
    // this factory's EF Core InMemory provider has no real change tracker to
    // prime, so a stale rowVersion cannot lose a race here (the same
    // InMemory-vs-real-SQL-Server split already documented on
    // FakeTicketRepository.SetRowVersion and TigerCsApiFactory). Asserting a
    // 409 over this host would test the provider, not the override.

    [Fact]
    public async Task AuditEntriesAreStillWrittenForTheAdministratorsActions()
    {
        var (client, adminEmployeeId) = await CreateAdministratorAsync();

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var intake = await response.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var audit = await db.AuditEntries.FirstOrDefaultAsync(e =>
            e.ActorEmployeeId == adminEmployeeId
            && e.Action == "CreateIntakeRecord"
            && e.EntityId == intake!.IntakeRecordId.ToString());

        Assert.NotNull(audit);
    }

    // ---------------------------------------------------------------
    // Requirement 9 — the one place granting authorization would have
    // conflicted with a business rule. Reported in the PR description.
    // ---------------------------------------------------------------

    /// <summary>
    /// Verification-session single-agent ownership (MVP-ERD.md §2.24) is a
    /// per-record business invariant, not a permission-matrix cell, so the
    /// override deliberately does not reach it: the administrator passes the
    /// CustomerVerification policy and reaches the application service (which
    /// is the correction's requirement), and the service then refuses the
    /// session because it belongs to another agent.
    ///
    /// <para>
    /// This costs the administrator nothing operationally — it can create
    /// its own session and use that end-to-end, as
    /// <see cref="CreateTicket_WithCustomerMatch_Returns201"/> does.
    /// Overriding it instead would let an administrator consume a session an
    /// agent was mid-way through and attribute the resulting requester
    /// snapshot to a verification they never performed, which is an audit
    /// integrity problem, not an access one. Business-rule change: ticket
    /// creation itself no longer consumes a VerificationSession at all (see
    /// TicketCreationAppService.CreateAsync's remarks), so this ownership
    /// rule is now exercised through the two calls that still do: reading a
    /// session directly, and reconciling a ticket against one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnotherAgentsVerificationSession_IsStillRefused_OwnershipIsNotAnAuthorizationPolicy()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);

        var unit = await (await agentClient.GetAsync("/api/crm/units/CRM-UNIT-1001"))
            .Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await agentClient.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts"))
            .Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();
        var agentsSession = await (await agentClient.PostAsJsonAsync(
                "/api/verification-sessions",
                new CreateVerificationSessionRequestDto(unit!.UnitReferenceId, contacts![0].ContactReferenceId, true, "ManualAgentConfirmation")))
            .Content.ReadFromJsonAsync<VerificationSessionResponseDto>();

        var (adminClient, _) = await CreateAdministratorAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await adminClient.GetAsync($"/api/verification-sessions/{agentsSession!.VerificationSessionId}")).StatusCode);

        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);
        var intake = await (await adminClient.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500009999", null, true, "1204", null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        var unverified = await (await adminClient.PostAsJsonAsync(
                "/api/tickets",
                new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.High, "AC unit not cooling")))
            .Content.ReadFromJsonAsync<TicketResponseDto>();

        var reconcileResponse = await adminClient.PostAsJsonAsync(
            $"/api/tickets/{unverified!.TicketId}/reconciliation",
            new ReconcileTicketRequestDto(agentsSession.VerificationSessionId, Convert.FromBase64String(unverified.RowVersion)));

        Assert.Equal(HttpStatusCode.Forbidden, reconcileResponse.StatusCode);
    }

    [Fact]
    public async Task DeactivatedAdministrator_IsStillRefused()
    {
        // The one authorization-shaped rule the override deliberately does
        // not satisfy: ActiveEmployeeRequirement is an IIdentityGateRequirement
        // (Security-Architecture.md §14, FR-ADM-02). The token below is
        // valid and unexpired; deactivation alone revokes it.
        var (client, employeeId) = await CreateAdministratorAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/roles")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var employee = await db.Employees.SingleAsync(e => e.EmployeeId == employeeId);
            employee.Deactivate(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null))).StatusCode);
    }
}
