using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// End-to-end against the real Api host (real routing, real authorization,
/// real EF Core-mapped schema — InMemory provider — the real MockCrmGateway/
/// MockPactGateway/MockTasleehGateway fixture data) — exercising the actual
/// sequence an agent follows: intake -> customer lookup (CRM/PACT/Tasleeh)
/// -> ticket creation. Business-rule change: customer lookup is enrichment,
/// never a Ticket creation gate — every test below that creates a ticket
/// without a customer match proves that directly.
/// </summary>
public class TicketingEndpointsTests : IClassFixture<TigerCsApiFactory>
{
    /// <summary>Matches MockCrmGateway's CRM-UNIT-1001/CRM-CONTACT-2002 fixture (Sara Yousef) — the standard "customer found" phone number for these tests.</summary>
    private const string PhoneWithCrmMatch = "+971500000001";
    private const string PhoneWithNoMatch = "+971500009999";

    private readonly TigerCsApiFactory _factory;

    public TicketingEndpointsTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string role = "CS Agent")
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return client;
    }

    [Fact]
    public async Task CreateIntakeRecord_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithCrmMatch, null, true, "1204", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FullLifecycle_IntakeLookupCreate_ProducesVerifiedTicket()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("General Inquiry", departmentId);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithCrmMatch, null, true, "1204", null));
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        Assert.Equal(HttpStatusCode.OK, lookupResponse.StatusCode);
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        var crmMatch = lookup!.Sources.Single(s => s.Source == "Crm");
        Assert.Equal("Found", crmMatch.Status);
        Assert.NotNull(crmMatch.UnitReferenceId);
        Assert.NotNull(crmMatch.ContactReferenceId);

        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake.IntakeRecordId, crmMatch.UnitReferenceId, crmMatch.ContactReferenceId, categoryId, (byte)PriorityLevel.High, "AC unit not cooling"));

        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.Equal("Verified", ticket!.VerificationStatus);
        Assert.Equal("Open", ticket.TicketStatus);
        Assert.Equal(crmMatch.UnitReferenceId, ticket.UnitReferenceId);
        Assert.StartsWith("TG-", ticket.TicketNumber);

        // Reusing an already-linked IntakeRecord is rejected, not silently re-executed.
        var replay = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake.IntakeRecordId, crmMatch.UnitReferenceId, crmMatch.ContactReferenceId, categoryId, (byte)PriorityLevel.High, "Second attempt"));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task CreateTicket_NoCustomerMatch_StillReturns201WithUnverifiedTicket()
    {
        // Business-rule change: CRM/PACT/Tasleeh finding nothing never blocks
        // ticket creation — even for a unit-related intake.
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithNoMatch, null, true, "9999", (byte)PriorityLevel.Critical));
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        Assert.All(lookup!.Sources, s => Assert.Equal("NotFound", s.Status));

        var response = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(intake.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Critical, "Flooding in lobby"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.Equal("Unverified", ticket!.VerificationStatus);
        Assert.Null(ticket.UnitReferenceId);
        Assert.Null(ticket.ContactReferenceId);
    }

    [Fact]
    public async Task CreateIntakeRecord_BlankPhoneNumber_Returns400_RecordNotCreated()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "", null, false, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CustomerLookup_DepartmentScopedToCrmOnly_SearchesOnlyCrm_NeverFallsBackToPactEvenThoughItHasAMatch()
    {
        // MockPactGateway has a real fixture match for +971500000002 — this
        // proves that a Department configured for CRM only never falls back
        // to PACT even when PACT would have found something.
        const string phoneWithPactMatchOnly = "+971500000002";

        var client = await CreateAuthenticatedClientAsync();
        var departmentId = await _factory.CreateDepartmentAsync("CRM-Only Dept " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        await _factory.SeedDepartmentCustomerLookupSourceAsync(departmentId, CustomerLookupSource.Crm);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", phoneWithPactMatchOnly, departmentId, false, null, null));
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        Assert.Equal(HttpStatusCode.OK, lookupResponse.StatusCode);
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();

        var source = Assert.Single(lookup!.Sources);
        Assert.Equal("Crm", source.Source);
        Assert.Equal("NotFound", source.Status);
    }

    [Fact]
    public async Task CustomerLookup_NoDepartmentSelected_SearchesAllThreeSources()
    {
        var client = await CreateAuthenticatedClientAsync();

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithNoMatch, null, false, null, null));
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();

        Assert.Equal(3, lookup!.Sources.Count);
        Assert.Contains(lookup.Sources, s => s.Source == "Crm");
        Assert.Contains(lookup.Sources, s => s.Source == "Pact");
        Assert.Contains(lookup.Sources, s => s.Source == "Tasleeh");
    }

    [Fact]
    public async Task CreateIntakeRecord_UnknownDepartmentId_Returns404()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithNoMatch, 999_999, false, null, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateTicket_CrmOutageDuringLookup_StillReturns201()
    {
        // Business-rule change: a source that cannot be reached at all
        // (Failed, not NotFound) still never blocks ticket creation.
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        // MockCrmGateway/MockPactGateway/MockTasleehGateway all trigger a
        // simulated outage when the searched value contains "OUTAGE".
        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500OUTAGE01", null, false, null, null));
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        Assert.Equal(HttpStatusCode.OK, lookupResponse.StatusCode);
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        Assert.All(lookup!.Sources, s => Assert.Equal("Failed", s.Status));

        var response = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(intake.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Medium, "General question"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateTicket_MissingCategory_Returns404_TicketRejected()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithNoMatch, null, false, null, null));
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, CategoryId: 999_999, (byte)PriorityLevel.Medium, "General billing question"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateTicket_NonUnitIntake_Returns201()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("General Inquiry", departmentId);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithNoMatch, null, false, null, null));
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        Assert.False(intake!.IsUnitRelated);

        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(intake.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Medium, "General billing question"));

        // Routes successfully from category alone — no Unit/CRM data of any kind was ever supplied.
        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.Equal("Unverified", ticket!.VerificationStatus);
        Assert.Equal("Open", ticket.TicketStatus);
        Assert.Null(ticket.UnitReferenceId);
        Assert.Null(ticket.ContactReferenceId);
        Assert.Equal(departmentId, ticket.OriginatingDepartmentId);
        Assert.StartsWith("TG-", ticket.TicketNumber);

        var detailResponse = await client.GetAsync($"/api/tickets/{ticket.TicketId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
    }

    // CS Agent/CS Supervisor by the CustomerVerification policy's own role
    // list; System Administrator by the ADR-0024 central override.
    public static IEnumerable<object[]> AllRolesWithExpectedAccess() =>
        Roles.All.Select(role => new object[]
        {
            role,
            role is Roles.CsAgent or Roles.CsSupervisor or Roles.SystemAdministrator
        });

    [Theory]
    [MemberData(nameof(AllRolesWithExpectedAccess))]
    public async Task CreateIntakeRecord_EveryRole_MatchesCustomerVerificationPolicy(string role, bool expectAuthorized)
    {
        var client = await CreateAuthenticatedClientAsync(role);

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithCrmMatch, null, true, "1204", null));

        Assert.Equal(expectAuthorized ? HttpStatusCode.Created : HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<(long TicketId, int DepartmentId, byte[] RowVersion)> CreateVerifiedTicketAsync(HttpClient creatorClient, string departmentPrefix = "Facilities")
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync(departmentPrefix + " " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        var intakeResponse = await creatorClient.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithCrmMatch, null, true, "1204", null));
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await creatorClient.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        var crmMatch = lookup!.Sources.Single(s => s.Source == "Crm");

        var ticketResponse = await creatorClient.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake.IntakeRecordId, crmMatch.UnitReferenceId, crmMatch.ContactReferenceId, categoryId, (byte)PriorityLevel.High, "AC unit not cooling"));
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();

        return (ticket!.TicketId, departmentId, Convert.FromBase64String(ticket.RowVersion));
    }

    [Fact]
    public async Task FullOperationsLifecycle_AssignWorkResolveClose_Succeeds()
    {
        var agentClient = await CreateAuthenticatedClientAsync(Roles.CsAgent);
        var (ticketId, departmentId, initialRowVersion) = await CreateVerifiedTicketAsync(agentClient);

        var (workerUsername, workerPassword, workerId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(workerId, departmentId);
        var workerClient = _factory.CreateClient();
        var loginResponse = await workerClient.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(workerUsername, workerPassword));
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        workerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        // PR correction: Department Employee holds no assignment capability
        // at all (not even self-claim) — a Department Head in the same
        // department assigns them instead.
        var (deptHeadUsername, deptHeadPassword, deptHeadId) = await _factory.SeedEmployeeAsync(Roles.DepartmentHead);
        await _factory.AssignPrimaryDepartmentAsync(deptHeadId, departmentId);
        var deptHeadClient = _factory.CreateClient();
        var deptHeadLoginResponse = await deptHeadClient.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(deptHeadUsername, deptHeadPassword));
        var deptHeadLogin = await deptHeadLoginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        deptHeadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deptHeadLogin!.AccessToken);

        // Department Employee cannot self-claim.
        var selfClaimAttempt = await workerClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(workerId, initialRowVersion));
        Assert.Equal(HttpStatusCode.Forbidden, selfClaimAttempt.StatusCode);

        var assignResponse = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(workerId, initialRowVersion));
        Assert.Equal(HttpStatusCode.OK, assignResponse.StatusCode);
        var afterAssign = await assignResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        // Start work.
        var statusResponse = await workerClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/status", new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(afterAssign!.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var afterStatus = await statusResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        // Resolve as the Department Employee who owns it.
        var resolveResponse = await workerClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "Fixed the AC unit.", null, null, Convert.FromBase64String(afterStatus!.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        var afterResolve = await resolveResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        // A Department Employee may never close (ISSUE-022).
        var forbiddenClose = await workerClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve!.RowVersion)));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenClose.StatusCode);

        // CS Agent closes it.
        var closeResponse = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        var closed = await closeResponse.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Closed", closed!.TicketStatus);

        // Notes are visible cross-department to CS.
        var noteResponse = await agentClient.PostAsJsonAsync($"/api/tickets/{ticketId}/notes", new CreateNoteRequestDto("Customer confirmed resolution."));
        Assert.Equal(HttpStatusCode.Created, noteResponse.StatusCode);

        var detailResponse = await agentClient.GetAsync($"/api/tickets/{ticketId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
    }

    [Fact]
    public async Task Assign_ByDepartmentHeadOfADifferentDepartment_Returns403_PreventsCrossDepartmentAssignment()
    {
        var agentClient = await CreateAuthenticatedClientAsync(Roles.CsAgent);
        var (ticketId, _, _) = await CreateVerifiedTicketAsync(agentClient);

        // Department Head genuinely holds Assign authority (PR correction) —
        // just not for a department they don't belong to, which is what
        // this test isolates.
        var (outsiderUsername, outsiderPassword, outsiderId) = await _factory.SeedEmployeeAsync(Roles.DepartmentHead);
        var otherDepartmentId = await _factory.CreateDepartmentAsync("Unrelated " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        await _factory.AssignPrimaryDepartmentAsync(outsiderId, otherDepartmentId);

        var outsiderClient = _factory.CreateClient();
        var loginResponse = await outsiderClient.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(outsiderUsername, outsiderPassword));
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        outsiderClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        var response = await outsiderClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(outsiderId, []));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_ByDepartmentHead_Returns403_TransferIsCsManagerOnly()
    {
        var agentClient = await CreateAuthenticatedClientAsync(Roles.CsAgent);
        var (ticketId, departmentId, _) = await CreateVerifiedTicketAsync(agentClient);

        var (deptHeadUsername, deptHeadPassword, deptHeadId) = await _factory.SeedEmployeeAsync(Roles.DepartmentHead);
        await _factory.AssignPrimaryDepartmentAsync(deptHeadId, departmentId);
        var deptHeadClient = _factory.CreateClient();
        var loginResponse = await deptHeadClient.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(deptHeadUsername, deptHeadPassword));
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        deptHeadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        var otherDepartmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);

        var response = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/transfer", new TransferTicketRequestDto(otherDepartmentId, "Misrouted", []));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ClosedTicket_AssignTransferStatusResolveClose_AllReturn422_NoFurtherStateChange()
    {
        var agentClient = await CreateAuthenticatedClientAsync(Roles.CsAgent);
        var (ticketId, departmentId, initialRowVersion) = await CreateVerifiedTicketAsync(agentClient);

        var (deptHeadUsername, deptHeadPassword, deptHeadId) = await _factory.SeedEmployeeAsync(Roles.DepartmentHead);
        await _factory.AssignPrimaryDepartmentAsync(deptHeadId, departmentId);
        var deptHeadClient = _factory.CreateClient();
        var deptHeadLoginResponse = await deptHeadClient.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(deptHeadUsername, deptHeadPassword));
        var deptHeadLogin = await deptHeadLoginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        deptHeadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deptHeadLogin!.AccessToken);

        var assignResponse = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(deptHeadId, initialRowVersion));
        var afterAssign = await assignResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        var statusResponse = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/status", new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(afterAssign!.RowVersion)));
        var afterStatus = await statusResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        var resolveResponse = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, Convert.FromBase64String(afterStatus!.RowVersion)));
        var afterResolve = await resolveResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        var closeResponse = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve!.RowVersion)));
        var closed = await closeResponse.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Closed", closed!.TicketStatus);
        var closedRowVersion = Convert.FromBase64String(closed.RowVersion);

        // Every mutating operation now rejects with 422 — no exceptions.
        var reassign = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(deptHeadId, closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reassign.StatusCode);

        var otherDepartmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var managerClient = await CreateAuthenticatedClientAsync(Roles.CsManager);
        var transfer = await managerClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/transfer", new TransferTicketRequestDto(otherDepartmentId, "n/a", closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, transfer.StatusCode);

        var statusChange = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/status", new ChangeStatusRequestDto("InProgress", closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, statusChange.StatusCode);

        var reResolve = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "n/a", null, null, closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reResolve.StatusCode);

        var reClose = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reClose.StatusCode);

        // Reading remains allowed throughout.
        var detail = await agentClient.GetAsync($"/api/tickets/{ticketId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var finalDetail = await detail.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Closed", finalDetail!.TicketStatus);

        var notes = await agentClient.GetAsync($"/api/tickets/{ticketId}/notes");
        Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
    }

    [Fact]
    public async Task GetQueue_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/tickets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
