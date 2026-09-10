using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// The Unclassified → Classified transition.
///
/// <para>
/// A ticket created from a Genesys inquiry exists before anyone has read the
/// request: its department is known, its category is not. These prove the
/// four things that state depends on — the same ticket is classified in
/// place, the classification is write-once, a category may not silently
/// re-route the ticket, and the SLA clock starts here rather than against
/// the provisional priority the ticket was created with.
/// </para>
/// </summary>
public class TicketClassificationAppServiceTests
{
    private static readonly DateTime CreatedAt = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        TicketClassificationAppService Service,
        FakeTicketRepository Tickets,
        FakeCategoryRepository Categories,
        FakePriorityRepository Priorities,
        FakeRequestTypeRepository RequestTypes,
        FakeWorkflowTemplateRepository WorkflowTemplates,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeTicketStatusHistoryRepository StatusHistory,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork,
        SlaServiceFixture Sla);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var categories = new FakeCategoryRepository();
        var priorities = new FakePriorityRepository();
        var requestTypes = new FakeRequestTypeRepository();
        var workflowTemplates = new FakeWorkflowTemplateRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var sla = new SlaServiceFixture(tickets, statusHistory: statusHistory, audit: audit, unitOfWork: unitOfWork);

        var service = new TicketClassificationAppService(
            tickets, categories, priorities, requestTypes, workflowTemplates, departmentAssignments,
            statusHistory, unitOfWork, audit, sla.DueDates, TimeProvider.System);

        return new Fixture(
            service, tickets, categories, priorities, requestTypes, workflowTemplates,
            departmentAssignments, statusHistory, audit, unitOfWork, sla);
    }

    /// <summary>
    /// Exactly what ingestion produces: department resolved, nothing else
    /// decided, and deliberately no SLA period.
    /// </summary>
    private static async Task<Ticket> SeedUnclassifiedTicketAsync(Fixture f, int departmentId = 2)
    {
        var ticket = Ticket.CreateUnclassified(
            "TG-CS-20260910-0001", departmentId, (byte)PriorityLevel.Medium,
            "Customer asked about an NOC", CreatedAt);
        await f.Tickets.AddAsync(ticket);
        return ticket;
    }

    private static ClassifyTicketRequestDto Request(int categoryId, byte priorityId = (byte)PriorityLevel.High, int? requestTypeId = null) =>
        new(categoryId, priorityId, requestTypeId, RowVersion: [1, 2, 3, 4, 5, 6, 7, 8]);

    // ---- The transition itself ----

    [Fact]
    public async Task ClassifyAsync_UpdatesTheSameTicket_AndNeverCreatesASecondOne()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId, "NOC Request");

        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, Request(category.CategoryId));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);

        // The load-bearing assertion: one ticket, before and after. A
        // customer who started one conversation keeps one ticket, with its
        // number, department and history intact.
        var stored = Assert.Single(f.Tickets.All);
        Assert.Equal(ticket.TicketId, stored.TicketId);
        Assert.Equal("TG-CS-20260910-0001", stored.TicketNumber);
        Assert.Equal(CreatedAt, stored.CreatedAtUtc);

        Assert.True(stored.IsClassified);
        Assert.Equal(category.CategoryId, stored.CategoryId);
        Assert.Equal((byte)PriorityLevel.High, stored.PriorityId);
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
        Assert.Contains(f.Audit.Written, w => w.Action == "ClassifyTicket" && w.EntityType == "Ticket");
    }

    [Fact]
    public async Task ClassifyAsync_IsWhereTheSlaClockStarts_ForATicketThatNeverHadOne()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);

        // While unclassified the ticket deliberately had no SLA period: the
        // policy is chosen by priority, and its priority was provisional.
        Assert.Equal(SlaState.NotApplicable, ticket.SlaState);
        Assert.Empty(f.Sla.SlaInstances.All);

        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, Request(category.CategoryId));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(SlaState.Running, ticket.SlaState);

        var instance = Assert.Single(f.Sla.SlaInstances.All);
        Assert.Equal(ticket.TicketId, instance.TicketId);
        // Selected from the real classification's priority, not the
        // provisional Medium the ticket was created with.
        Assert.Equal((byte)PriorityLevel.High, instance.PriorityId);

        // The dimension genuinely moved, so it is on the history like every
        // other dimension change.
        Assert.Contains(f.StatusHistory.Added, h =>
            h.Dimension == TicketStatusDimension.SlaState
            && h.OldValue == (byte)SlaState.NotApplicable
            && h.NewValue == (byte)SlaState.Running);
    }

    [Fact]
    public async Task ClassifyAsync_BackdatesTheSlaClockToTheTicketsCreation_SoClassifyingLateBuysNoExtraTime()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);

        await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, Request(category.CategoryId));

        var instance = Assert.Single(f.Sla.SlaInstances.All);
        // Not "now". The customer's clock runs from when their inquiry
        // arrived, whatever hour later an agent got to it.
        Assert.Equal(ticket.CreatedAtUtc, instance.PeriodStartAtUtc);
    }

    [Fact]
    public async Task ClassifyAsync_WithARequestType_PinsThePublishedWorkflowVersion()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedPending(workflowId: 100));
        var requestType = f.RequestTypes.Add(new RequestType(
            ticket.CurrentDepartmentId, "NOC Request", template.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true));

        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId,
            Request(category.CategoryId, requestTypeId: requestType.RequestTypeId));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(requestType.RequestTypeId, ticket.RequestTypeId);
        // Pinned exactly as a ticket classified at creation would have been.
        Assert.Equal(template.WorkflowTemplateId, ticket.WorkflowTemplateId);
    }

    // ---- What classification refuses ----

    [Fact]
    public async Task ClassifyAsync_ATicketThatAlreadyHasACategory_IsRefused_NotSilentlyReCategorised()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260910-0002", departmentId: 2, categoryId: 7,
            priorityId: (byte)PriorityLevel.Medium, "AC not cooling", CreatedAt);
        await f.Tickets.AddAsync(ticket);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);

        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, Request(category.CategoryId));

        Assert.Equal(TicketMutationOutcome.AlreadyClassified, result.Outcome);
        Assert.Equal(7, ticket.CategoryId);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Empty(f.Sla.SlaInstances.All);
    }

    [Fact]
    public async Task ClassifyAsync_ACategoryFromAnotherDepartment_IsRefused_BecauseItWouldReRouteTheTicket()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f, departmentId: 2);
        // The department was settled by the inquiry, before the ticket
        // existed. Classification names the request; it does not transfer.
        var foreignCategory = f.Categories.Seed(departmentId: 9, name: "Leasing Renewal");

        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, Request(foreignCategory.CategoryId));

        Assert.Equal(TicketMutationOutcome.CategoryDepartmentMismatch, result.Outcome);
        Assert.False(ticket.IsClassified);
        Assert.Equal(2, ticket.CurrentDepartmentId);
        Assert.Equal(SlaState.NotApplicable, ticket.SlaState);
    }

    [Fact]
    public async Task ClassifyAsync_AnInactiveCategory_IsRefused()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var retired = f.Categories.Seed(ticket.CurrentDepartmentId, "Retired", isActive: false);

        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, Request(retired.CategoryId));

        Assert.Equal(TicketMutationOutcome.CategoryNotFound, result.Outcome);
        Assert.False(ticket.IsClassified);
    }

    [Fact]
    public async Task ClassifyAsync_APriorityThatDoesNotExist_IsRefused_BeforeAnySlaPolicyIsSelected()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);

        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, Request(category.CategoryId, priorityId: 99));

        Assert.Equal(TicketMutationOutcome.PriorityNotFound, result.Outcome);
        Assert.False(ticket.IsClassified);
        Assert.Empty(f.Sla.SlaInstances.All);
    }

    [Fact]
    public async Task ClassifyAsync_AClosedTicket_IsRefused()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();

        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, Request(category.CategoryId));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.False(ticket.IsClassified);
    }

    [Fact]
    public async Task ClassifyAsync_ByAnEmployeeOutsideTheTicketsDepartment_IsForbidden()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);

        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.DepartmentEmployee], ticket.TicketId, Request(category.CategoryId));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.False(ticket.IsClassified);
    }

    [Fact]
    public async Task ClassifyAsync_AnUnknownTicket_IsNotFound()
    {
        var f = CreateService();

        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticketId: 4242, Request(1));

        Assert.Equal(TicketMutationOutcome.NotFound, result.Outcome);
    }
}
