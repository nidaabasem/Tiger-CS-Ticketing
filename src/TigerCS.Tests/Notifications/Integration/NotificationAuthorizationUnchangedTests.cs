using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Notifications.Integration;

/// <summary>
/// This increment adds no endpoint and changes no authorization rule
/// (MVP-Implementation-Backlog.md's "no changes to existing ticket
/// authorization rules"). These tests prove that rather than assert it in a
/// commit message.
/// </summary>
public class NotificationAuthorizationUnchangedTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public NotificationAuthorizationUnchangedTests(TigerCsApiFactory factory)
    {
        _factory = factory;
        _factory.EmailSender.Clear();
    }

    private async Task<HttpClient> CreateClientAsync(string role)
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();

        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return client;
    }

    /// <summary>
    /// Drives the real intake → CRM lookup → verification → ticket sequence.
    ///
    /// <para>
    /// <paramref name="setupClient"/> defaults to <paramref name="client"/>
    /// itself, because MVP-ERD.md §2.24's single-agent-ownership rule means
    /// only the session's own owner may consume it — a caller handed somebody
    /// else's session is refused on <i>that</i> rule, not on the endpoint's
    /// authorization policy, and conflating the two would let this test pass
    /// for the wrong reason. A caller who cannot reach the setup endpoints at
    /// all passes an agent as <paramref name="setupClient"/>, so the only
    /// thing the final POST measures is whether that role may create a ticket.
    /// </para>
    /// </summary>
    private async Task<HttpResponseMessage> AttemptTicketCreationAsync(HttpClient client, HttpClient? setupClient = null)
    {
        setupClient ??= client;

        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        var intake = await (await setupClient.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", true, "1204", null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var unit = await (await setupClient.GetAsync("/api/crm/units/CRM-UNIT-1001"))
            .Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await setupClient.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts"))
            .Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();

        var session = await (await setupClient.PostAsJsonAsync(
                "/api/verification-sessions",
                new CreateVerificationSessionRequestDto(unit!.UnitReferenceId, contacts![0].ContactReferenceId, true, "ManualAgentConfirmation")))
            .Content.ReadFromJsonAsync<VerificationSessionResponseDto>();

        return await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketFromVerificationRequestDto(
                intake!.IntakeRecordId, session!.VerificationSessionId, categoryId,
                (byte)PriorityLevel.High, "AC unit not cooling"));
    }

    /// <summary>
    /// Ticket creation stays CS Agent/CS Supervisor only. Adding an Outbox
    /// write to that path must not widen who may reach it.
    /// </summary>
    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.ReportingUser)]
    public async Task RolesThatCouldNotCreateTicketsBefore_StillCannot(string role)
    {
        var client = await CreateClientAsync(role);

        var response = await AttemptTicketCreationAsync(client, await CreateClientAsync(Roles.CsAgent));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A refused request writes no Outbox message — authorization is checked before anything is queued, so a forbidden caller cannot cause an email.</summary>
    [Fact]
    public async Task ARefusedTicketCreation_QueuesNothing()
    {
        var client = await CreateClientAsync(Roles.ReportingUser);
        var before = (await _factory.GetOutboxMessagesAsync()).Count;

        var response = await AttemptTicketCreationAsync(client, await CreateClientAsync(Roles.CsAgent));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, (await _factory.GetOutboxMessagesAsync()).Count);
    }

    /// <summary>
    /// ADR-0024's central System Administrator override still applies to
    /// ticket creation, and the Outbox write inside that path works for it
    /// exactly as for any other authorized caller — no role-specific branch
    /// anywhere.
    /// </summary>
    [Fact]
    public async Task SystemAdministrator_StillCreatesTicketsAndQueuesTheAcknowledgement()
    {
        var client = await CreateClientAsync(Roles.SystemAdministrator);

        var response = await AttemptTicketCreationAsync(client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<TicketResponseDto>();

        Assert.Contains(
            await _factory.GetOutboxMessagesAsync(),
            m => m.EventType == OutboxEventTypes.TicketCreated
                && m.Payload.Contains($"\"ticketId\":{ticket!.TicketId}", StringComparison.Ordinal));
    }

    /// <summary>
    /// The structural property ADR-0024 requirement 4 asks for, applied to a
    /// module written after it: no type in the Notifications module names any
    /// role at all. The override therefore cannot be forgotten here, because
    /// there is no role logic to forget it in.
    /// </summary>
    [Fact]
    public void NoNotificationsModuleTypeNamesARole()
    {
        var notificationTypes = typeof(TicketAcknowledgementHandler).Assembly
            .GetTypes()
            .Where(t => (t.Namespace ?? string.Empty).StartsWith("TigerCS.Application.Modules.Notifications", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(notificationTypes);

        var offenders = notificationTypes
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (Field: field, Value: field.GetRawConstantValue() as string))
            .Where(entry => entry.Value is not null && Roles.All.Contains(entry.Value, StringComparer.Ordinal))
            .Select(entry => $"{entry.Field.DeclaringType!.FullName}.{entry.Field.Name}")
            .ToList();

        Assert.Empty(offenders);
    }
}
