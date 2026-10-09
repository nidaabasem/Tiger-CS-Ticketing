using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>A person manually assigning or transferring a ticket is not a reply to the customer: First Response keeps running.</summary>
public class FirstResponseManualAssignTransferTests
{
    [Fact]
    public async Task ManualAssignmentAndTransfer_DoNotStopFirstResponse()
    {
        var f = new GenesysServiceFixture();
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var finance = f.Departments.AddDepartment("Finance", "FIN");
        var created = await f.Ingestion.IngestAsync(
            Guid.NewGuid(),
            new GenesysInquiryDto("conv-manual", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: cs.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, created.Outcome);
        var ticket = f.Tickets.All.Single();

        var assignee = Guid.NewGuid();
        var receiver = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(assignee, cs.DepartmentId, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(receiver, finance.DepartmentId, true, DateTime.UtcNow, null));
        var service = new TicketAssignmentAppService(
            f.Tickets, f.TicketAssignments, f.DepartmentAssignments, f.Departments, f.UnitOfWork, f.Audit,
            TimeProvider.System, f.DepartmentSettings,
            new TicketAutoAssignmentService(f.AssignmentRules, f.DepartmentSettings, f.DepartmentAssignments, f.TicketAssignments, f.Audit));

        Assert.Equal(TicketMutationOutcome.Success,
            (await service.AssignAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(assignee, ticket.RowVersion))).Outcome);
        Assert.Null(ticket.FirstHumanResponseAtUtc);

        Assert.Equal(TicketMutationOutcome.Success,
            (await service.TransferAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
                new TransferTicketRequestDto(finance.DepartmentId, "Belongs to Finance", ticket.RowVersion, receiver))).Outcome);
        Assert.Equal(finance.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.FirstHumanResponseAtUtc);
        var period = Assert.Single(f.Sla.SlaInstances.All, i => i.PeriodEndAtUtc is null);
        Assert.False(period.FirstResponseBreached);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "RecordFirstResponse");
    }
}
