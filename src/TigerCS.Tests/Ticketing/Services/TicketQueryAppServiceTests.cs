using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

public class TicketQueryAppServiceTests
{
    private sealed record Fixture(TicketQueryAppService Service, FakeTicketRepository Tickets, FakeUserDepartmentAssignmentRepository DepartmentAssignments);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        return new Fixture(new TicketQueryAppService(tickets, departmentAssignments), tickets, departmentAssignments);
    }

    private static async Task<Ticket> SeedTicketAsync(FakeTicketRepository repo, int departmentId)
    {
        var ticket = Ticket.CreateVerified(
            $"TG-CS-20260821-{departmentId:D4}", departmentId, 10, 20, 5, (byte)PriorityLevel.High, "Issue", DateTime.UtcNow);
        await repo.AddAsync(ticket);
        return ticket;
    }

    [Fact]
    public async Task GetQueueAsync_DepartmentEmployee_OnlySeesOwnDepartmentTickets()
    {
        var f = CreateService();
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 2, true, DateTime.UtcNow, null));
        await SeedTicketAsync(f.Tickets, 2);
        await SeedTicketAsync(f.Tickets, 3);

        var result = await f.Service.GetQueueAsync(
            employeeId, [Roles.DepartmentEmployee],
            new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 50));

        Assert.Single(result.Items);
        Assert.Equal(2, result.Items[0].CurrentDepartmentId);
    }

    [Fact]
    public async Task GetQueueAsync_CsManager_SeesAllDepartments()
    {
        var f = CreateService();
        await SeedTicketAsync(f.Tickets, 2);
        await SeedTicketAsync(f.Tickets, 3);

        var result = await f.Service.GetQueueAsync(
            Guid.NewGuid(), [Roles.CsManager],
            new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 50));

        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task GetDetailAsync_CallerOutsideDepartmentScope_ReturnsForbidden_DoesNotLeakTicket()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, 2);
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 999, true, DateTime.UtcNow, null));

        var result = await f.Service.GetDetailAsync(employeeId, [Roles.DepartmentEmployee], ticket.TicketId);

        Assert.Equal(TicketQueryOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task GetDetailAsync_UnverifiedTicket_ExposesUnverifiedAndNullReferences_NeverFabricatesVerified()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260821-0099", 2, 5, (byte)PriorityLevel.Critical, "Flooding", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(TicketQueryOutcome.Success, result.Outcome);
        Assert.Equal("Unverified", result.Response!.VerificationStatus);
        Assert.Null(result.Response.UnitReferenceId);
        Assert.Null(result.Response.ContactReferenceId);
    }

    [Fact]
    public async Task GetDetailAsync_UnknownTicket_ReturnsNotFound()
    {
        var f = CreateService();

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], 999);

        Assert.Equal(TicketQueryOutcome.NotFound, result.Outcome);
    }
}
