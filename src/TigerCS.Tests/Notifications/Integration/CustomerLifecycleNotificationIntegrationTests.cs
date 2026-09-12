using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Notifications.Integration;

/// <summary>
/// The customer email pipeline end to end through the real API host:
/// resolve → close → reopen over HTTP, each committing its Outbox event
/// with the state change, then the real dispatcher routing each event to
/// the registered handler and the in-memory recording adapter receiving
/// exactly one customer email per transition. Also the regression check
/// that the three lifecycle endpoints' contracts are unchanged.
/// </summary>
public class CustomerLifecycleNotificationIntegrationTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public CustomerLifecycleNotificationIntegrationTests(TigerCsApiFactory factory)
    {
        _factory = factory;
        _factory.EmailSender.Clear();
    }

    private async Task<(HttpClient Client, Guid EmployeeId)> CreateClientAsync(string role)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    /// <summary>A CRM-verified ticket whose snapshot contact channel is the fixture's email contact (index 0).</summary>
    private async Task<(TicketResponseDto Ticket, int DepartmentId)> CreateTicketAsync(HttpClient client, int contactIndex = 0)
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        var unit = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001"))
            .Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts"))
            .Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();

        var createResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake!.IntakeRecordId, unit!.UnitReferenceId, contacts![contactIndex].ContactReferenceId, categoryId,
                (byte)PriorityLevel.High, "AC unit not cooling"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        return ((await createResponse.Content.ReadFromJsonAsync<TicketResponseDto>())!, departmentId);
    }

    private async Task<(TicketResponseDto Ticket, HttpClient Worker, HttpClient Agent, TicketDetailDto AfterResolve)> CreateAndResolveAsync(int contactIndex = 0)
    {
        var (agent, _) = await CreateClientAsync(Roles.CsAgent);
        var (manager, _) = await CreateClientAsync(Roles.CsManager);
        var (worker, workerId) = await CreateClientAsync(Roles.DepartmentEmployee);
        var (ticket, departmentId) = await CreateTicketAsync(agent, contactIndex);
        await _factory.AssignPrimaryDepartmentAsync(workerId, departmentId);

        var assignResponse = await manager.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/assignment",
            new AssignTicketRequestDto(workerId, Convert.FromBase64String(ticket.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, assignResponse.StatusCode);
        var afterAssign = await assignResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        var statusResponse = await worker.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/status",
            new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(afterAssign!.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var afterStatus = await statusResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        var resolveResponse = await worker.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/resolution",
            new ResolveTicketRequestDto(
                "Resolved", "Replaced the thermostat.", null, null, Convert.FromBase64String(afterStatus!.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        var afterResolve = (await resolveResponse.Content.ReadFromJsonAsync<TicketDetailDto>())!;
        Assert.Equal("Resolved", afterResolve.TicketStatus);

        return (ticket, worker, agent, afterResolve);
    }

    private async Task<OutboxMessage> OutboxMessageFor(long ticketId, string eventType) =>
        Assert.Single(
            await _factory.GetOutboxMessagesAsync(),
            m => m.EventType == eventType && m.Payload.Contains($"\"ticketId\":{ticketId}", StringComparison.Ordinal));

    [Fact]
    public async Task ResolvingATicket_CommitsAPendingTicketResolvedEventAndSendsNothingInline()
    {
        var (ticket, _, _, _) = await CreateAndResolveAsync();

        var message = await OutboxMessageFor(ticket.TicketId, OutboxEventTypes.TicketResolved);

        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.Equal(0, message.Attempts);
        Assert.DoesNotContain(
            _factory.EmailSender.Recorded,
            e => e.Subject.StartsWith("Your request has been resolved", StringComparison.Ordinal)
                && e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveCloseReopen_EachDeliversExactlyOneCustomerEmailThroughTheDispatcher()
    {
        var (ticket, _, agent, afterResolve) = await CreateAndResolveAsync();

        var closeResponse = await agent.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        var afterClose = (await closeResponse.Content.ReadFromJsonAsync<TicketDetailDto>())!;

        var reopenResponse = await agent.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/reopen",
            new ReopenTicketRequestDto("Customer reports the fault is back.", Convert.FromBase64String(afterClose.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, reopenResponse.StatusCode);

        var result = await _factory.RunOutboxDispatchAsync();
        await _factory.RunOutboxDispatchAsync(); // a second pass must change nothing

        Assert.Equal(0, result.DeadLettered);
        Assert.Equal(0, result.Failed);

        var mine = _factory.EmailSender.Recorded
            .Where(e => e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal))
            .Select(e => e.Subject)
            .OrderBy(s => s)
            .ToList();
        Assert.Equal(
            [
                $"Your request has been closed – Ticket {ticket.TicketNumber}",
                $"Your request has been received – Ticket {ticket.TicketNumber}",
                $"Your request has been reopened – Ticket {ticket.TicketNumber}",
                $"Your request has been resolved – Ticket {ticket.TicketNumber}"
            ],
            mine);
        Assert.All(
            _factory.EmailSender.Recorded.Where(e => e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal)),
            e => Assert.Equal("ahmed.alfarsi@example.com", e.ToAddress));

        var notifications = await _factory.GetNotificationsAsync(ticket.TicketId);
        Assert.Equal(
            [NotificationType.Acknowledgement, NotificationType.Resolved, NotificationType.Closed, NotificationType.Reopened],
            notifications.OrderBy(n => n.NotificationType).Select(n => n.NotificationType).ToArray());
        Assert.All(notifications, n => Assert.Equal(NotificationDeliveryStatus.Sent, n.DeliveryStatus));

        foreach (var eventType in new[] { OutboxEventTypes.TicketResolved, OutboxEventTypes.TicketClosed, OutboxEventTypes.TicketReopened })
        {
            var message = await OutboxMessageFor(ticket.TicketId, eventType);
            Assert.Equal(OutboxMessageStatus.Processed, message.Status);
            Assert.Equal(1, message.Attempts);
        }

        Assert.Contains(await _factory.GetNotificationAuditAsync(), a =>
            a.Action == NotificationAuditActions.NotificationDeliverySucceeded
            && a.AfterValue!.Contains("NotificationType=Reopened", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneOnlyCustomer_LifecycleNotificationsAreSkippedAndTheOperationStillSucceeds()
    {
        var (ticket, _, _, _) = await CreateAndResolveAsync(contactIndex: 1);

        var result = await _factory.RunOutboxDispatchAsync();

        Assert.Equal(0, result.DeadLettered);
        Assert.DoesNotContain(_factory.EmailSender.Recorded, e => e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal));
        var resolved = Assert.Single(await _factory.GetNotificationsAsync(ticket.TicketId), n => n.NotificationType == NotificationType.Resolved);
        Assert.Equal(NotificationDeliveryStatus.Skipped, resolved.DeliveryStatus);
        Assert.Equal(OutboxMessageStatus.Processed, (await OutboxMessageFor(ticket.TicketId, OutboxEventTypes.TicketResolved)).Status);
    }
}
