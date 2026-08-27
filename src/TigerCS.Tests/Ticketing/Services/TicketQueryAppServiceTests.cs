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

    // ---- Ticket Details CRM Buyer / manual / legacy unit display (root-cause fix:
    // TicketDetailDto never carried the CrmBuyer*/Manual* snapshot fields the Ticket
    // entity already persists, so the read path silently dropped them) ----

    [Fact]
    public async Task GetDetailAsync_CrmBuyerVerifiedTicket_ExposesTheExactSelectedCustomerProjectAndUnit()
    {
        var f = CreateService();
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            "TG-CS-20260827-0001", 2,
            crmBuyerCustomerId: 555, crmBuyerLeadId: 306756, crmBuyerUnitId: 100003691, crmBuyerProjectId: 42,
            crmBuyerCustomerName: "Walid Jalanbo", crmBuyerProjectName: "Nobles Tower", crmBuyerUnitNumber: "2508",
            categoryId: 5, priorityId: (byte)PriorityLevel.Medium, requestSummary: "AC not cooling", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(TicketQueryOutcome.Success, result.Outcome);
        var dto = result.Response!;
        Assert.Equal("Verified", dto.VerificationStatus);
        Assert.Equal("Walid Jalanbo", dto.CrmBuyerCustomerName);
        Assert.Equal("Nobles Tower", dto.CrmBuyerProjectName);
        // The exact unit the agent selected — not merely "a" unit or the first match.
        Assert.Equal("2508", dto.CrmBuyerUnitNumber);
        Assert.Equal(100003691, dto.CrmBuyerUnitId);
        Assert.Equal(306756, dto.CrmBuyerLeadId);
        Assert.Null(dto.ManualProjectName);
        Assert.Null(dto.ManualUnitNumber);
        Assert.Null(dto.UnitReferenceId);
        Assert.Null(dto.ContactReferenceId);
    }

    [Fact]
    public async Task GetDetailAsync_ManualNoCrmMatchTicket_ExposesManualProjectAndUnit_NotVerified()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260827-0002", 2, 5, (byte)PriorityLevel.Low, "Water leak",
            DateTime.UtcNow, manualProjectName: "Sapphire Residences", manualUnitNumber: "1204");
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        var dto = result.Response!;
        Assert.Equal("Unverified", dto.VerificationStatus);
        Assert.Equal("Sapphire Residences", dto.ManualProjectName);
        Assert.Equal("1204", dto.ManualUnitNumber);
        Assert.Null(dto.CrmBuyerUnitId);
        Assert.Null(dto.CrmBuyerCustomerName);
    }

    [Fact]
    public async Task GetDetailAsync_LegacyVerifiedTicket_StaysBackwardCompatible_NoCrmOrManualFields()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, 2);

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        var dto = result.Response!;
        Assert.Equal("Verified", dto.VerificationStatus);
        Assert.Equal(10, dto.UnitReferenceId);
        Assert.Equal(20, dto.ContactReferenceId);
        Assert.Null(dto.CrmBuyerUnitId);
        Assert.Null(dto.ManualProjectName);
        Assert.Null(dto.ManualUnitNumber);
    }

    [Fact]
    public async Task GetDetailAsync_PlainUnverifiedTicket_NullCrmAndManualFields_DoesNotThrow()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260827-0003", 2, 5, (byte)PriorityLevel.Critical, "General inquiry", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        var dto = result.Response!;
        Assert.Null(dto.CrmBuyerUnitId);
        Assert.Null(dto.CrmBuyerCustomerName);
        Assert.Null(dto.ManualProjectName);
        Assert.Null(dto.ManualUnitNumber);
        Assert.Null(dto.UnitReferenceId);
        Assert.Null(dto.ContactReferenceId);
    }
}
