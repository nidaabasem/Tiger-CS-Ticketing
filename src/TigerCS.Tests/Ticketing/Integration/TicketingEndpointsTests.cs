using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// End-to-end against the real Api host (real routing, real authorization,
/// real EF Core-mapped schema — InMemory provider — the real MockCrmGateway
/// fixture data) — exercising the actual sequence an agent follows: intake
/// -> CRM lookup -> verification -> ticket creation, and ISSUE-006's
/// provisional/queued fallback.
/// </summary>
public class TicketingEndpointsTests : IClassFixture<TigerCsApiFactory>
{
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
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", true, "1204", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FullLifecycle_IntakeVerifyCreate_ProducesVerifiedTicket()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("General Inquiry", departmentId);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", true, "1204", null));
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var unitResponse = await client.GetAsync("/api/crm/units/CRM-UNIT-1001");
        unitResponse.EnsureSuccessStatusCode();
        var unit = await unitResponse.Content.ReadFromJsonAsync<UnitVerificationResponseDto>();

        var contactsResponse = await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts");
        contactsResponse.EnsureSuccessStatusCode();
        var contacts = await contactsResponse.Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();

        var sessionResponse = await client.PostAsJsonAsync(
            "/api/verification-sessions",
            new CreateVerificationSessionRequestDto(unit!.UnitReferenceId, contacts![0].ContactReferenceId, true, "ManualAgentConfirmation"));
        sessionResponse.EnsureSuccessStatusCode();
        var session = await sessionResponse.Content.ReadFromJsonAsync<VerificationSessionResponseDto>();

        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketFromVerificationRequestDto(
                intake!.IntakeRecordId, session!.VerificationSessionId, categoryId, (byte)PriorityLevel.High, "AC unit not cooling"));

        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.Equal("Verified", ticket!.VerificationStatus);
        Assert.Equal("Open", ticket.TicketStatus);
        Assert.Equal(unit.UnitReferenceId, ticket.UnitReferenceId);
        Assert.StartsWith("TG-", ticket.TicketNumber);

        // Reusing the same (now-consumed) session a second time is rejected, not silently re-executed.
        var replay = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketFromVerificationRequestDto(
                intake.IntakeRecordId, session.VerificationSessionId, categoryId, (byte)PriorityLevel.High, "Second attempt"));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task CreateProvisional_CriticalPriorityDuringOutage_Returns201WithPendingCrmVerification()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", true, "1204", (byte)PriorityLevel.Critical));
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.PostAsJsonAsync(
            "/api/tickets/provisional",
            new CreateProvisionalTicketRequestDto(intake!.IntakeRecordId, categoryId, (byte)PriorityLevel.Critical, "Flooding in lobby"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.Equal("PendingCrmVerification", ticket!.VerificationStatus);
        Assert.Null(ticket.UnitReferenceId);
        Assert.Null(ticket.ContactReferenceId);
    }

    [Fact]
    public async Task CreateProvisional_LowPriorityDuringOutage_Returns200QueuedNotACreatedTicket()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", true, "0507", (byte)PriorityLevel.Low));
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.PostAsJsonAsync(
            "/api/tickets/provisional",
            new CreateProvisionalTicketRequestDto(intake!.IntakeRecordId, categoryId, (byte)PriorityLevel.Low, "Leaking tap"));

        // Not an error — ISSUE-006's approved "remains queued" outcome (200, not 201).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        Assert.Equal("PendingCrmVerification", queued!.CrmVerificationStatus);
        Assert.Null(queued.LinkedTicketId);
    }

    public static IEnumerable<object[]> AllRolesWithExpectedAccess() =>
        Roles.All.Select(role => new object[] { role, role is Roles.CsAgent or Roles.CsSupervisor });

    [Theory]
    [MemberData(nameof(AllRolesWithExpectedAccess))]
    public async Task CreateIntakeRecord_EveryRole_MatchesCustomerVerificationPolicy(string role, bool expectAuthorized)
    {
        var client = await CreateAuthenticatedClientAsync(role);

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", true, "1204", null));

        Assert.Equal(expectAuthorized ? HttpStatusCode.Created : HttpStatusCode.Forbidden, response.StatusCode);
    }
}
