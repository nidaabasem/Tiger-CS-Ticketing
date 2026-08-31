using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Customer -> Previous Ticket History (this increment). Covers the CRM-
/// verified identity path (CrmBuyerCustomerId), the phone-snapshot fallback
/// for tickets with no CrmBuyerCustomerId, department-visibility
/// authorization reuse, sort order, limits, and null-safety.
/// </summary>
public class CustomerHistoryAppServiceTests
{
    private sealed record Fixture(
        CustomerHistoryAppService Service,
        FakeTicketRepository Tickets,
        FakeIntakeRecordRepository IntakeRecords,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var intakeRecords = new FakeIntakeRecordRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var queryService = new TicketQueryAppService(tickets, departmentAssignments);
        return new Fixture(
            new CustomerHistoryAppService(tickets, intakeRecords, queryService), tickets, intakeRecords, departmentAssignments);
    }

    private static async Task<Ticket> SeedCrmBuyerTicketAsync(
        FakeTicketRepository repo, int departmentId, int crmBuyerCustomerId, string unitNumber, DateTime createdAtUtc, string? customerName = "Walid Jalanbo")
    {
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            $"TG-CS-{createdAtUtc:yyyyMMdd}-{Guid.NewGuid():N}"[..24], departmentId,
            crmBuyerCustomerId: crmBuyerCustomerId, crmBuyerLeadId: 1, crmBuyerUnitId: Random.Shared.Next(1, 999999), crmBuyerProjectId: 1,
            crmBuyerCustomerName: customerName, crmBuyerProjectName: "Nobles Tower", crmBuyerUnitNumber: unitNumber,
            categoryId: 5, priorityId: (byte)PriorityLevel.Medium, requestSummary: "Issue", createdAtUtc);
        await repo.AddAsync(ticket);
        return ticket;
    }

    // ---------------------------------------------------------------
    // 1-3: CRM-verified identity
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetByCrmCustomerIdAsync_NoPreviousTickets_ReturnsEmptyHistory()
    {
        var f = CreateService();

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], crmBuyerCustomerId: 493575);

        Assert.Equal("Verified", result.VerificationType);
        Assert.Equal(0, result.TotalTickets);
        Assert.Equal(0, result.OpenTickets);
        Assert.Equal(0, result.ClosedTickets);
        Assert.Empty(result.Tickets);
    }

    [Fact]
    public async Task GetByCrmCustomerIdAsync_OnePreviousTicket_ReturnsIt()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", DateTime.UtcNow);

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 493575);

        Assert.Equal(1, result.TotalTickets);
        var row = Assert.Single(result.Tickets);
        Assert.Equal(ticket.TicketId, row.TicketId);
        Assert.Equal("2508", row.UnitNumber);
    }

    [Fact]
    public async Task GetByCrmCustomerIdAsync_MultipleTicketsAcrossMultipleUnits_ReturnsAllOfThem()
    {
        var f = CreateService();
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", DateTime.UtcNow.AddDays(-3));
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2608", DateTime.UtcNow.AddDays(-2));
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2810", DateTime.UtcNow.AddDays(-1));

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 493575);

        Assert.Equal(3, result.TotalTickets);
        Assert.Equal(["2810", "2608", "2508"], result.Tickets.Select(t => t.UnitNumber));
    }

    // ---------------------------------------------------------------
    // 4: exact CrmBuyerCustomerId isolation
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetByCrmCustomerIdAsync_SamePhoneTwoDifferentCrmCustomerIds_NeverSharesHistory()
    {
        var f = CreateService();
        // Both tickets carry the same real-world phone number (captured on
        // their IntakeRecords) but were matched to two different CRM
        // customers — a phone number is not a trusted unique identity.
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, crmBuyerCustomerId: 111, "2508", DateTime.UtcNow);
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, crmBuyerCustomerId: 222, "9001", DateTime.UtcNow);

        var historyFor111 = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 111);
        var historyFor222 = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 222);

        Assert.Equal(1, historyFor111.TotalTickets);
        Assert.Equal(1, historyFor222.TotalTickets);
        Assert.DoesNotContain(historyFor111.Tickets, t => t.UnitNumber == "9001");
        Assert.DoesNotContain(historyFor222.Tickets, t => t.UnitNumber == "2508");
    }

    // ---------------------------------------------------------------
    // 5: current ticket excluded from Ticket Details previous history
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetForTicketAsync_ExcludesTheCurrentTicketFromItsOwnHistory()
    {
        var f = CreateService();
        var older = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", DateTime.UtcNow.AddDays(-1));
        var current = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2608", DateTime.UtcNow);

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], current.TicketId);

        Assert.Equal(CustomerHistoryOutcome.Success, result.Outcome);
        Assert.Equal(1, result.Response!.TotalTickets);
        var row = Assert.Single(result.Response.Tickets);
        Assert.Equal(older.TicketId, row.TicketId);
        Assert.DoesNotContain(result.Response.Tickets, t => t.TicketId == current.TicketId);
    }

    // ---------------------------------------------------------------
    // 6: newest first
    // ---------------------------------------------------------------

    [Fact]
    public async Task History_IsSortedNewestFirst()
    {
        var f = CreateService();
        var oldest = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 777, "A", DateTime.UtcNow.AddDays(-5));
        var middle = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 777, "B", DateTime.UtcNow.AddDays(-3));
        var newest = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 777, "C", DateTime.UtcNow.AddDays(-1));

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 777);

        Assert.Equal([newest.TicketId, middle.TicketId, oldest.TicketId], result.Tickets.Select(t => t.TicketId));
    }

    // ---------------------------------------------------------------
    // 7-8: manual/unverified customer history by phone snapshot, clearly marked
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetForTicketAsync_NoCrmBuyerCustomerId_FallsBackToPhoneSnapshot_MarkedUnverified()
    {
        var f = CreateService();
        const string phone = "+971501234567";

        var olderTicket = Ticket.CreateUnverified(
            "TG-CS-20260101-0001", 2, 5, (byte)PriorityLevel.Low, "Older issue", DateTime.UtcNow.AddDays(-1),
            manualProjectName: "Sapphire Residences", manualUnitNumber: "1204");
        await f.Tickets.AddAsync(olderTicket);
        var olderIntake = new IntakeRecord(Channel.Phone, phone, 2, false, null, null, Guid.NewGuid(), DateTime.UtcNow.AddDays(-1));
        await f.IntakeRecords.AddAsync(olderIntake);
        olderIntake.LinkToTicket(olderTicket.TicketId, olderTicket.VerificationStatus, hasSelectedUnit: false);

        var currentTicket = Ticket.CreateUnverified(
            "TG-CS-20260102-0001", 2, 5, (byte)PriorityLevel.Low, "Current issue", DateTime.UtcNow,
            manualProjectName: "Sapphire Residences", manualUnitNumber: "1204");
        await f.Tickets.AddAsync(currentTicket);
        var currentIntake = new IntakeRecord(Channel.Phone, phone, 2, false, null, null, Guid.NewGuid(), DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(currentIntake);
        currentIntake.LinkToTicket(currentTicket.TicketId, currentTicket.VerificationStatus, hasSelectedUnit: false);

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], currentTicket.TicketId);

        Assert.Equal(CustomerHistoryOutcome.Success, result.Outcome);
        Assert.Equal("Unverified", result.Response!.VerificationType);
        Assert.Equal(phone, result.Response.PhoneNumberSnapshot);
        Assert.Null(result.Response.CrmBuyerCustomerId);
        var row = Assert.Single(result.Response.Tickets);
        Assert.Equal(olderTicket.TicketId, row.TicketId);
    }

    // ---------------------------------------------------------------
    // 9: authorization — never wider than the ticket queue's own visibility
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetByCrmCustomerIdAsync_DepartmentEmployee_OnlySeesTicketsInOwnDepartment()
    {
        var f = CreateService();
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 2, true, DateTime.UtcNow, null));
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", DateTime.UtcNow);
        await SeedCrmBuyerTicketAsync(f.Tickets, 3, 493575, "2608", DateTime.UtcNow);

        var result = await f.Service.GetByCrmCustomerIdAsync(employeeId, [Roles.DepartmentEmployee], 493575);

        Assert.Equal(1, result.TotalTickets);
        Assert.Equal("2508", Assert.Single(result.Tickets).UnitNumber);
    }

    [Fact]
    public async Task GetForTicketAsync_CallerOutsideDepartmentScope_ReturnsForbidden_DoesNotLeakHistory()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", DateTime.UtcNow);
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 999, true, DateTime.UtcNow, null));

        var result = await f.Service.GetForTicketAsync(employeeId, [Roles.DepartmentEmployee], ticket.TicketId);

        Assert.Equal(CustomerHistoryOutcome.Forbidden, result.Outcome);
        Assert.Null(result.Response);
    }

    // ---------------------------------------------------------------
    // 11: preview limit
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetByCrmCustomerIdAsync_LimitsTheReturnedTickets_ButNotTheTotalCount()
    {
        var f = CreateService();
        for (var i = 0; i < 8; i++)
        {
            await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, $"{2500 + i}", DateTime.UtcNow.AddDays(-i));
        }

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 493575, limit: 5);

        Assert.Equal(8, result.TotalTickets);
        Assert.Equal(5, result.Tickets.Count);
    }

    // ---------------------------------------------------------------
    // 12: TicketId is preserved for detail links
    // ---------------------------------------------------------------

    [Fact]
    public async Task History_RowsCarryTheirOwnRealTicketId()
    {
        var f = CreateService();
        var a = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", DateTime.UtcNow.AddDays(-1));
        var b = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2608", DateTime.UtcNow);

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 493575);

        Assert.Equal([b.TicketId, a.TicketId], result.Tickets.Select(t => t.TicketId));
        Assert.All(result.Tickets, t => Assert.True(t.TicketId > 0));
    }

    // ---------------------------------------------------------------
    // 14: null CRM/customer data never throws
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetForTicketAsync_NoCrmBuyerCustomerIdAndNoLinkedIntakeRecord_ReturnsEmptyUnverifiedHistory_DoesNotThrow()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified("TG-CS-20260101-0002", 2, 5, (byte)PriorityLevel.Low, "No intake link", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(CustomerHistoryOutcome.Success, result.Outcome);
        Assert.Equal("Unverified", result.Response!.VerificationType);
        Assert.Equal(0, result.Response.TotalTickets);
        Assert.Empty(result.Response.Tickets);
    }

    [Fact]
    public async Task GetForTicketAsync_UnknownTicket_ReturnsNotFound()
    {
        var f = CreateService();

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], 999);

        Assert.Equal(CustomerHistoryOutcome.NotFound, result.Outcome);
    }
}
