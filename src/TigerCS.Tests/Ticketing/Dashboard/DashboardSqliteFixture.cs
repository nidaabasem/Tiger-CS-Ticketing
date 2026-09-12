using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Seed;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Dashboard;

/// <summary>
/// A real relational database (SQLite in-memory, the real EF Core-mapped
/// schema) behind the real <see cref="DashboardQueryRepository"/> and
/// <see cref="TicketRepository"/>, so every dashboard aggregate and every
/// drill-down filter is proven as SQL — grouped and counted by the engine,
/// never by an in-memory fake that could hide a translation failure.
/// </summary>
public sealed class DashboardSqliteFixture : IDisposable
{
    /// <summary>Tickets.RowVersion is a SQL Server rowversion, which SQLite cannot generate — the SQLite schema gives it a default. Nothing under test changes.</summary>
    private sealed class SqliteTigerCsDbContext(DbContextOptions<TigerCsDbContext> options) : TigerCsDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Ticket>().Property(t => t.RowVersion).HasDefaultValueSql("X'0000000000000000'");
        }
    }

    public sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private int _ticketSequence;

    /// <summary>The evaluation instant every query runs at — mid-day so "today" has room on both sides.</summary>
    public static readonly DateTime Now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    public int CustomerServiceId { get; private set; }
    public int CollectionsId { get; private set; }
    public int RegistrationId { get; private set; }
    public int CsCategoryId { get; private set; }
    public int CollectionsCategoryId { get; private set; }
    public int RegistrationCategoryId { get; private set; }
    public int CsRequestTypeId { get; private set; }
    public int CollectionsRequestTypeId { get; private set; }
    public Guid CsAgentId { get; } = Guid.NewGuid();
    public Guid CollectionsHeadId { get; } = Guid.NewGuid();
    public Guid RegistrationEmployeeId { get; } = Guid.NewGuid();

    /// <summary>The owner a non-Open seed ticket gets when a test names none: the lifecycle requires an owner before work starts, and this employee is never a KPI subject.</summary>
    public Guid LifecycleWorkerId { get; } = Guid.NewGuid();

    public DashboardSqliteFixture()
    {
        _connection.Open();
        using var context = CreateContext();
        context.Database.EnsureCreated();
        SeedReferenceData(context);
    }

    public TigerCsDbContext CreateContext() => new SqliteTigerCsDbContext(
        new DbContextOptionsBuilder<TigerCsDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
            .Options);

    /// <summary>The real application service over the real repositories — scope resolved from the real membership table.</summary>
    public DashboardAppService CreateService(TigerCsDbContext context, DateTime? nowUtc = null)
    {
        var time = new FixedTimeProvider(nowUtc ?? Now);
        var assignments = new UserDepartmentAssignmentRepository(context);
        var tickets = new TicketRepository(context);
        var queries = new TicketQueryAppService(tickets, assignments, new FakeTicketResolutionRepository(), ReopenPolicy.Default, time);
        return new DashboardAppService(tickets, queries, time, new DashboardQueryRepository(context), assignments);
    }

    public TicketQueryAppService CreateQueryService(TigerCsDbContext context, DateTime? nowUtc = null)
    {
        var time = new FixedTimeProvider(nowUtc ?? Now);
        var assignments = new UserDepartmentAssignmentRepository(context);
        return new TicketQueryAppService(new TicketRepository(context), assignments, new FakeTicketResolutionRepository(), ReopenPolicy.Default, time);
    }

    private void SeedReferenceData(TigerCsDbContext context)
    {
        var customerService = new Department("Customer Service", "CS");
        var collections = new Department("Collections", "COL");
        var registration = new Department("Registration", "REG");
        context.Departments.AddRange(customerService, collections, registration);

        foreach (var priority in new[]
        {
            new Priority((byte)PriorityLevel.Critical, "Critical", 1),
            new Priority((byte)PriorityLevel.High, "High", 2),
            new Priority((byte)PriorityLevel.Medium, "Medium", 3),
            new Priority((byte)PriorityLevel.Low, "Low", 4)
        })
        {
            context.Priorities.Add(priority);
        }

        context.Channels.AddRange(ChannelReferenceData.Channels());
        context.SaveChanges();

        CustomerServiceId = customerService.DepartmentId;
        CollectionsId = collections.DepartmentId;
        RegistrationId = registration.DepartmentId;

        var csCategory = new Category("General Inquiry", CustomerServiceId);
        var collectionsCategory = new Category("Payment Plan", CollectionsId);
        var registrationCategory = new Category("Title Deed", RegistrationId);
        context.Categories.AddRange(csCategory, collectionsCategory, registrationCategory);

        var workflow = new Workflow("STD", "Standard", null, Now.AddDays(-100));
        context.Workflows.Add(workflow);
        context.SaveChanges();

        var csRequestType = new RequestType(CustomerServiceId, "Complaint", workflow.WorkflowId, (byte)PriorityLevel.Medium, true, true, true, true);
        var collectionsRequestType = new RequestType(CollectionsId, "Payment Reminder", workflow.WorkflowId, (byte)PriorityLevel.Medium, true, true, true, true);
        context.RequestTypes.AddRange(csRequestType, collectionsRequestType);

        AddEmployee(context, CsAgentId, "Amal Agent");
        AddEmployee(context, CollectionsHeadId, "Hadi Head");
        AddEmployee(context, RegistrationEmployeeId, "Rana Registrar");
        AddEmployee(context, LifecycleWorkerId, "Walid Worker");
        context.SaveChanges();

        CsCategoryId = csCategory.CategoryId;
        CollectionsCategoryId = collectionsCategory.CategoryId;
        RegistrationCategoryId = registrationCategory.CategoryId;
        CsRequestTypeId = csRequestType.RequestTypeId;
        CollectionsRequestTypeId = collectionsRequestType.RequestTypeId;

        context.UserDepartmentAssignments.AddRange(
            new UserDepartmentAssignment(CsAgentId, CustomerServiceId, true, Now.AddDays(-50), null),
            new UserDepartmentAssignment(CollectionsHeadId, CollectionsId, true, Now.AddDays(-50), null),
            new UserDepartmentAssignment(RegistrationEmployeeId, RegistrationId, true, Now.AddDays(-50), null));
        context.SaveChanges();
    }

    private static void AddEmployee(TigerCsDbContext context, Guid employeeId, string displayName)
    {
        context.Users.Add(new ApplicationUser
        {
            Id = employeeId,
            UserName = $"user-{employeeId:N}",
            NormalizedUserName = $"USER-{employeeId:N}",
            Email = $"user-{employeeId:N}@example.test",
            SecurityStamp = Guid.NewGuid().ToString()
        });
        context.Employees.Add(new Employee(employeeId, displayName, isGeynessStaff: false, Now.AddDays(-60)));
    }

    /// <summary>
    /// One ticket with exactly the facts a test names. Status transitions
    /// follow the real lifecycle methods (never a direct status write):
    /// Resolved goes Open → InProgress → Resolved; Closed adds Close().
    /// </summary>
    public Ticket AddTicket(
        TigerCsDbContext context,
        int departmentId,
        DateTime? createdAtUtc = null,
        byte? priorityId = (byte)PriorityLevel.Medium,
        TicketStatus status = TicketStatus.Open,
        Guid? owner = null,
        byte? channelId = null,
        int? requestTypeId = null,
        bool slaBreached = false,
        DateTime? resolutionDueAtUtc = null,
        int? transferToDepartmentId = null,
        string? customerName = null)
    {
        var sequence = Interlocked.Increment(ref _ticketSequence);
        var created = createdAtUtc ?? Now.AddHours(-1);
        var categoryId = departmentId == CustomerServiceId ? CsCategoryId
            : departmentId == CollectionsId ? CollectionsCategoryId
            : RegistrationCategoryId;

        var ticketNumber = $"TG-TST-20260910-{sequence:D5}";
        var summary = $"Seed ticket {sequence}";
        var ticket = customerName is not null
            // The CRM Buyer snapshot path is the only one that carries a
            // customer name on the ticket; created through the real factory.
            ? Ticket.CreateVerifiedFromCrmBuyer(
                ticketNumber, departmentId,
                crmBuyerCustomerId: sequence, crmBuyerLeadId: sequence, crmBuyerUnitId: sequence, crmBuyerProjectId: sequence,
                crmBuyerCustomerName: customerName, crmBuyerProjectName: "Tiger Tower", crmBuyerUnitNumber: $"U-{sequence}",
                categoryId: categoryId, priorityId: priorityId ?? (byte)PriorityLevel.Medium, requestSummary: summary, createdAtUtc: created)
            : priorityId is { } priority
                ? Ticket.CreateUnverified(ticketNumber, departmentId, categoryId, priority, summary, created)
                : Ticket.CreateUnclassified(ticketNumber, departmentId, summary, created);

        if (requestTypeId is { } requestType)
        {
            ticket.ClassifyRequestType(requestType);
        }

        if (transferToDepartmentId is { } target)
        {
            ticket.TransferToDepartment(target);
        }

        // The real lifecycle refuses to start work on an ownerless ticket,
        // so any status past Open needs an owner — the named one, or the
        // fixture's neutral worker.
        if (owner is { } ownerId)
        {
            ticket.AssignTo(ownerId);
        }
        else if (status != TicketStatus.Open)
        {
            ticket.AssignTo(LifecycleWorkerId);
        }

        switch (status)
        {
            case TicketStatus.Open:
                break;
            case TicketStatus.InProgress:
                ticket.ChangeStatus(TicketStatus.InProgress);
                break;
            case TicketStatus.PendingCustomer:
            case TicketStatus.PendingThirdParty:
                ticket.ChangeStatus(TicketStatus.InProgress);
                ticket.ChangeStatus(status);
                break;
            case TicketStatus.Resolved:
                ticket.ChangeStatus(TicketStatus.InProgress);
                ticket.Resolve(ResolutionOutcome.Resolved, null);
                break;
            case TicketStatus.Closed:
                ticket.ChangeStatus(TicketStatus.InProgress);
                ticket.Resolve(ResolutionOutcome.Resolved, null);
                ticket.Close();
                break;
        }

        if (slaBreached)
        {
            ticket.MarkSlaBreached();
        }

        context.Tickets.Add(ticket);
        context.SaveChanges();

        if (channelId is { } channel)
        {
            context.TicketInteractions.Add(TicketInteraction.CreateLocal(ticket.TicketId, channel, "+971500000000", created, isOriginatingInteraction: true));
        }

        if (resolutionDueAtUtc is { } dueAt)
        {
            // A deadline in the past is a legitimate seed (an overdue
            // ticket); the period's clock simply started before it.
            var clockStart = dueAt < created ? dueAt.AddHours(-1) : created;
            var period = TicketSlaInstance.OpenInitialPeriod(ticket.TicketId, priorityId ?? (byte)PriorityLevel.Medium, clockStart, dueAt, dueAt);
            if (slaBreached)
            {
                period.MarkBreached(SlaDeadlineType.Resolution);
            }

            context.TicketSlaInstances.Add(period);
        }

        context.SaveChanges();
        return ticket;
    }

    /// <summary>A Pending approval cycle on a ticket, targeted exactly as a configured requirement would target it.</summary>
    public TicketApproval AddPendingApproval(TigerCsDbContext context, Ticket ticket, RequestTypeApprovalRequirement requirement, Guid requestedBy)
    {
        var approval = TicketApproval.Request(ticket.TicketId, requirement, requestedBy, Now.AddHours(-2), null, Guid.NewGuid());
        context.TicketApprovals.Add(approval);
        context.SaveChanges();
        return approval;
    }

    public void Dispose() => _connection.Dispose();
}
