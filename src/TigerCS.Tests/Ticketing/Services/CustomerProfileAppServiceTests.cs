using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Customer Details/Profile (Overview/Contact Info/Units): ticket-anchored
/// identity and authorization, live CRM data via the reused
/// CrmBuyerLookupAppService, and graceful degradation for every non-Found
/// outcome (never a live CRM call away from Customer History's own
/// contract, which this test file does not touch).
/// </summary>
public class CustomerProfileAppServiceTests
{
    private sealed record Fixture(
        CustomerProfileAppService Service,
        FakeTicketRepository Tickets,
        FakeIntakeRecordRepository IntakeRecords,
        FakeCrmBuyerLookupGateway CrmGateway,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var intakeRecords = new FakeIntakeRecordRepository();
        var crmGateway = new FakeCrmBuyerLookupGateway();
        var crmBuyerLookupAppService = new CrmBuyerLookupAppService(crmGateway, NullLogger<CrmBuyerLookupAppService>.Instance);
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var queryService = new TicketQueryAppService(tickets, departmentAssignments);
        return new Fixture(
            new CustomerProfileAppService(tickets, intakeRecords, crmBuyerLookupAppService, queryService),
            tickets, intakeRecords, crmGateway, departmentAssignments);
    }

    private static async Task<Ticket> SeedCrmBuyerTicketAsync(FakeTicketRepository repo, FakeIntakeRecordRepository intakeRecords, int departmentId, int crmBuyerCustomerId, string phoneNumber)
    {
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            $"TG-CS-{Guid.NewGuid():N}"[..20], departmentId,
            crmBuyerCustomerId: crmBuyerCustomerId, crmBuyerLeadId: 1, crmBuyerUnitId: 101, crmBuyerProjectId: 10,
            crmBuyerCustomerName: "Walid Jalanbo", crmBuyerProjectName: "Nobles Tower", crmBuyerUnitNumber: "2508",
            categoryId: 5, priorityId: (byte)PriorityLevel.Medium, requestSummary: "Issue", DateTime.UtcNow);
        await repo.AddAsync(ticket);

        var intake = new IntakeRecord(Channel.Phone, phoneNumber, departmentId, false, null, null, Guid.NewGuid(), DateTime.UtcNow);
        await intakeRecords.AddAsync(intake);
        intake.LinkToTicket(ticket.TicketId, ticket.VerificationStatus, hasSelectedUnit: true);

        return ticket;
    }

    private static CrmBuyerMatchDto Buyer(int customerId, params CrmBuyerUnitDto[] units) => new(
        new CrmCustomerDto(customerId, "Walid Jalanbo", "وليد جلنبو", "+971501234567", "walid@example.test"), units);

    private static CrmBuyerUnitDto Unit(int unitId, string unitNumber, string projectName, int leadStatus = 8, string? leadStatusName = "Sold", int unitType = 2, int? floor = 12) => new(
        LeadId: 1, LeadStatus: leadStatus, LeadStatusName: leadStatusName, UnitId: unitId, UnitNumber: unitNumber,
        UnitStatus: 3, UnitType: unitType, FloorNumber: floor, ProjectId: 10, ProjectName: projectName,
        ProjectArabicName: null, CustomerType: 1, CustomerTypeName: "Buyer");

    [Fact]
    public async Task GetForTicketAsync_CrmFindsTheCustomer_ReturnsFoundWithAllEligibleUnits()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.Success(
        [
            Buyer(493575, Unit(101, "2508", "Nobles Tower"), Unit(202, "2608", "Nobles Tower"), Unit(303, "9001", "Sky Tower"))
        ]));

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(CustomerProfileOutcome.Success, result.Outcome);
        var profile = result.Response!;
        Assert.Equal("Found", profile.Status);
        Assert.Equal(493575, profile.CrmBuyerCustomerId);
        Assert.Equal("Walid Jalanbo", profile.FullNameEnglish);
        Assert.Equal("وليد جلنبو", profile.FullNameArabic);
        Assert.Equal("+971501234567", profile.MobileNumber);
        Assert.Equal("walid@example.test", profile.Email);
        // All eligible units for the customer, not just the current ticket's own unit (101).
        Assert.Equal(3, profile.Units.Count);
        Assert.Contains(profile.Units, u => u.UnitNumber == "2608");
        Assert.Contains(profile.Units, u => u.UnitNumber == "9001");
    }

    [Fact]
    public async Task GetForTicketAsync_TicketNotCrmVerified_ReturnsNotCrmVerifiedStatus_NoCrmCall()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified("TG-CS-20260101-0001", 2, 5, (byte)PriorityLevel.Low, "No CRM match", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(CustomerProfileOutcome.Success, result.Outcome);
        Assert.Equal("NotCrmVerified", result.Response!.Status);
        Assert.Null(result.Response.CrmBuyerCustomerId);
        Assert.Empty(result.Response.Units);
        Assert.Equal(0, f.CrmGateway.CallCount);
    }

    [Fact]
    public async Task GetForTicketAsync_CrmNoLongerFindsAMatch_ReturnsNotFoundInCrm_KeepsTheTicketsOwnCustomerId()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.NotFound());

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("NotFoundInCrm", result.Response!.Status);
        Assert.Equal(493575, result.Response.CrmBuyerCustomerId);
        Assert.Null(result.Response.FullNameEnglish);
        Assert.Empty(result.Response.Units);
    }

    [Fact]
    public async Task GetForTicketAsync_CrmUnavailable_ReturnsCrmUnavailableStatus_KeepsTheTicketsOwnCustomerId()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.Unavailable());

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("CrmUnavailable", result.Response!.Status);
        Assert.Equal(493575, result.Response.CrmBuyerCustomerId);
    }

    [Fact]
    public async Task GetForTicketAsync_CrmDataIntegrityConflict_ReturnsAmbiguousCustomerMatchStatus_NoUnitsOrNames()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.AmbiguousCustomerMatch());

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("AmbiguousCustomerMatch", result.Response!.Status);
        Assert.Equal(493575, result.Response.CrmBuyerCustomerId);
        Assert.Empty(result.Response.Units);
        Assert.Null(result.Response.FullNameEnglish);
    }

    [Fact]
    public async Task GetForTicketAsync_UnknownTicket_ReturnsNotFound()
    {
        var f = CreateService();

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], 999);

        Assert.Equal(CustomerProfileOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task GetForTicketAsync_CallerOutsideDepartmentScope_ReturnsForbidden_DoesNotCallCrm()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 999, true, DateTime.UtcNow, null));

        var result = await f.Service.GetForTicketAsync(employeeId, [Roles.DepartmentEmployee], ticket.TicketId);

        Assert.Equal(CustomerProfileOutcome.Forbidden, result.Outcome);
        Assert.Null(result.Response);
        Assert.Equal(0, f.CrmGateway.CallCount);
    }

    [Fact]
    public async Task GetForTicketAsync_DepartmentEmployeeInScope_Returns200()
    {
        var f = CreateService();
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 2, true, DateTime.UtcNow, null));
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.Success([Buyer(493575, Unit(101, "2508", "Nobles Tower"))]));

        var result = await f.Service.GetForTicketAsync(employeeId, [Roles.DepartmentEmployee], ticket.TicketId);

        Assert.Equal(CustomerProfileOutcome.Success, result.Outcome);
        Assert.Equal("Found", result.Response!.Status);
    }
}
