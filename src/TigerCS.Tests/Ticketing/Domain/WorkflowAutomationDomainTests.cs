using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Tests.Ticketing.Domain;

/// <summary>
/// Domain invariants of the Workflow/Automation phase-2 entities:
/// interaction context (Genesys optional, Face-to-Face local), structured
/// pending records, request-type classification, and assignment rules.
/// </summary>
public class WorkflowAutomationDomainTests
{
    private static readonly DateTime Now = new(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc);

    // ---- Interaction context ----

    [Fact]
    public void GenesysContext_CanCarryTheFullInteractionContext()
    {
        var context = TicketInteractionContext.CreateFromGenesys(
            ticketId: 1, Channel.Phone, "+971500000001",
            genesysConversationId: "conv-8842", calledNumber: "+97142223333",
            genesysQueueId: "q-77", genesysQueueName: "CS Main Queue",
            genesysAgentId: "agent-5", genesysAgentName: "Genesys Agent",
            interactionStartedAtUtc: Now.AddMinutes(-3), direction: "Inbound", Now);

        Assert.Equal(InteractionContextSource.Genesys, context.Source);
        Assert.Equal("conv-8842", context.GenesysConversationId);
        Assert.Equal("q-77", context.GenesysQueueId);
        Assert.Equal("CS Main Queue", context.GenesysQueueName);
        Assert.Equal("+97142223333", context.CalledNumber);
        Assert.Equal("+971500000001", context.CustomerPhone);
    }

    [Fact]
    public void GenesysContext_EveryFieldExceptConversationIdIsOptional()
    {
        // The exact Genesys contract is not finalized — only the
        // conversation id (the traceability link) is mandatory.
        var context = TicketInteractionContext.CreateFromGenesys(
            ticketId: 1, Channel.Phone, "+971500000001",
            genesysConversationId: "conv-1", calledNumber: null,
            genesysQueueId: null, genesysQueueName: null,
            genesysAgentId: null, genesysAgentName: null,
            interactionStartedAtUtc: null, direction: null, Now);

        Assert.Equal(InteractionContextSource.Genesys, context.Source);
        Assert.Null(context.GenesysQueueId);
        Assert.Null(context.GenesysAgentId);
        Assert.Null(context.CalledNumber);

        Assert.Throws<ArgumentException>(() => TicketInteractionContext.CreateFromGenesys(
            1, Channel.Phone, "+971500000001",
            genesysConversationId: " ", null, null, null, null, null, null, null, Now));
    }

    [Fact]
    public void FaceToFaceContext_NeverCarriesGenesysFields_ButStillRequiresCustomerPhone()
    {
        var context = TicketInteractionContext.CreateLocal(1, Channel.FaceToFaceKiosk, "+971500000001", Now);

        Assert.Equal(InteractionContextSource.Ticketing, context.Source);
        Assert.Equal(Channel.FaceToFaceKiosk, context.ChannelId);
        Assert.Equal("+971500000001", context.CustomerPhone);
        Assert.Null(context.GenesysConversationId);
        Assert.Null(context.GenesysQueueId);
        Assert.Null(context.GenesysQueueName);
        Assert.Null(context.GenesysAgentId);
        Assert.Null(context.GenesysAgentName);
        Assert.Null(context.CalledNumber);

        // The phone stays mandatory — it is the CRM/PACT/Tasleeh
        // verification identity input, Genesys or not.
        Assert.Throws<ArgumentException>(() => TicketInteractionContext.CreateLocal(1, Channel.FaceToFaceKiosk, " ", Now));
    }

    // ---- Request-type classification ----

    [Fact]
    public void ClassifyRequestType_IsWriteOnce()
    {
        var ticket = Ticket.CreateUnverified("TG-CS-1", 1, 5, (byte)PriorityLevel.Medium, "AC issue", Now);

        ticket.ClassifyRequestType(7);
        Assert.Equal(7, ticket.RequestTypeId);

        Assert.Throws<TicketRequestTypeAlreadySetException>(() => ticket.ClassifyRequestType(8));
        Assert.Equal(7, ticket.RequestTypeId);
    }

    // ---- Structured pending ----

    [Fact]
    public void PendingRecord_RequiresAReason()
    {
        Assert.Throws<ArgumentException>(() => new TicketPendingRecord(
            1, PendingKind.Customer, "  ", TicketStatus.InProgress, Guid.NewGuid(), Now, Guid.NewGuid()));
    }

    [Fact]
    public void PendingRecord_CapturesActorTimestampReasonAndPreviousStatus_AndResumeClosesItOnce()
    {
        var startedBy = Guid.NewGuid();
        var record = new TicketPendingRecord(
            1, PendingKind.Customer, "Missing passport copy", TicketStatus.InProgress, startedBy, Now, Guid.NewGuid());

        Assert.Equal(PendingKind.Customer, record.Kind);
        Assert.Equal("Missing passport copy", record.Reason);
        Assert.Equal(TicketStatus.InProgress, record.PreviousStatus);
        Assert.Equal(startedBy, record.StartedByEmployeeId);
        Assert.Null(record.ResumedAtUtc);
        Assert.Null(record.PausedDuration);

        var resumedBy = Guid.NewGuid();
        record.Resume(resumedBy, Now.AddHours(30));

        Assert.Equal(resumedBy, record.ResumedByEmployeeId);
        Assert.Equal(Now.AddHours(30), record.ResumedAtUtc);
        Assert.Equal(TimeSpan.FromHours(30), record.PausedDuration);

        Assert.Throws<InvalidOperationException>(() => record.Resume(Guid.NewGuid(), Now.AddHours(31)));
    }

    // ---- Assignment rules ----

    [Fact]
    public void TeamRule_KeepsOwnershipUnambiguous()
    {
        var primary = Guid.NewGuid();
        var memberA = Guid.NewGuid();
        var memberB = Guid.NewGuid();

        // The primary is never duplicated into the member list; duplicates collapse.
        var rule = RequestTypeAssignmentRule.ForTeam(1, primary, [memberA, memberB, memberA, primary], "AC Team");

        Assert.Equal(AssignmentMode.Team, rule.Mode);
        Assert.Equal(primary, rule.PrimaryEmployeeId);
        Assert.Equal(2, rule.Members.Count);
        Assert.DoesNotContain(rule.Members, m => m.EmployeeId == primary);

        // A "team" of just the primary is a specific-employee rule, not a team.
        Assert.Throws<ArgumentException>(() => RequestTypeAssignmentRule.ForTeam(1, primary, [primary], "Solo"));
        Assert.Throws<ArgumentException>(() => RequestTypeAssignmentRule.ForTeam(1, Guid.Empty, [memberA], null));
    }

    [Fact]
    public void QueueRule_CarriesNoTarget_AndSpecificEmployeeRequiresOne()
    {
        var queueRule = RequestTypeAssignmentRule.ForDepartmentQueue(1);
        Assert.Equal(AssignmentMode.DepartmentQueue, queueRule.Mode);
        Assert.Null(queueRule.PrimaryEmployeeId);
        Assert.Empty(queueRule.Members);

        Assert.Throws<ArgumentException>(() => RequestTypeAssignmentRule.ForSpecificEmployee(1, Guid.Empty));
    }
}
