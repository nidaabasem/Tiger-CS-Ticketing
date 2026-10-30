using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Genesys call transfers between agents are handling history: every agent who handled the customer is preserved, the first
/// handler stays the first handler, and none of it is an escalation, a counted transfer, or a change to ticket ownership.
/// Runs against the REAL ingestion / update / agent-context services over the shared fixture.
/// </summary>
public class GenesysAgentHandlingHistoryTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string AgentA = "ga-layla-0001";
    private const string AgentB = "ga-omar-0002";
    private const string AgentC = "ga-sara-0003";

    private static GenesysInquiryDto Inquiry(string conversationId, int departmentId, string? agentId = null, string? agentName = null) =>
        new(conversationId, GenesysChannel.Phone,
            CustomerPhone: "+971500000001", DepartmentId: departmentId, AgentId: agentId, AgentName: agentName);

    private static async Task<(GenesysServiceFixture F, long TicketId, Guid A, Guid B)> CallAnsweredByAAsync(string conversationId)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var a = f.MapAgent(AgentA, displayName: "Layla");
        var b = f.MapAgent(AgentB, displayName: "Omar");
        var result = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry(conversationId, department.DepartmentId, AgentA, "Layla"));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return (f, result.Ticket!.TicketId, a, b);
    }

    private static Task TransferAsync(GenesysServiceFixture f, long ticketId, string conversationId, string toAgent, string toName) =>
        f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(
            conversationId, Routing: new GenesysRoutingUpdateDto(QueueId: "q-cs", QueueName: "CS Queue", AgentId: toAgent, AgentName: toName)));

    [Fact]
    public async Task TheAgentWhoAnswered_IsPreservedAsFirstHandler_InTheInteractionAndTheEventHistory()
    {
        var (f, ticketId, a, _) = await CallAnsweredByAAsync("conv-first");

        Assert.Equal(a, f.InteractionFor("conv-first")!.HandledByUserId);
        var connected = Assert.Single(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.GenesysAgentConnected);
        Assert.Equal(ticketId, connected.TicketId);
        Assert.Equal(a, connected.ToEmployeeId);
        Assert.Null(connected.TransferReasonCode);
        Assert.False(connected.IsEscalation);
    }

    [Fact]
    public async Task ACallTransferBetweenAgents_IsRecorded_WithoutBeingAnEscalationOrACountedTransfer()
    {
        var (f, ticketId, a, b) = await CallAnsweredByAAsync("conv-xfer");

        await TransferAsync(f, ticketId, "conv-xfer", AgentB, "Omar");

        var transfer = Assert.Single(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.GenesysCallTransferred);
        Assert.Equal(a, transfer.ActorEmployeeId);       // transferred by the previous agent
        Assert.Equal(b, transfer.ToEmployeeId);          // received by the next agent
        Assert.Null(transfer.TransferReasonCode);        // no reason: a call transfer is not "unable to resolve"
        Assert.False(transfer.IsEscalation);
        Assert.Contains(AgentA, transfer.Note);
        Assert.Contains(AgentB, transfer.Note);
        // It is handling history only: no ticket transfer / escalation event exists.
        Assert.DoesNotContain(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.TicketTransferred);
    }

    [Fact]
    public async Task TheFirstHandler_StaysTheFirstHandler_AfterTheCallIsTransferred()
    {
        var (f, ticketId, a, _) = await CallAnsweredByAAsync("conv-keep-first");

        await TransferAsync(f, ticketId, "conv-keep-first", AgentB, "Omar");

        var interaction = f.InteractionFor("conv-keep-first")!;
        Assert.Equal(a, interaction.HandledByUserId);
        Assert.Equal(AgentA, interaction.GenesysAgentUserId);
        Assert.Equal(AgentB, interaction.GenesysAgentId);   // the current routing names the new agent
    }

    [Fact]
    public async Task EveryAgentWhoHandledAMultiHopTransfer_IsPreserved()
    {
        var (f, ticketId, a, b) = await CallAnsweredByAAsync("conv-hops");
        var c = f.MapAgent(AgentC, displayName: "Sara");

        await TransferAsync(f, ticketId, "conv-hops", AgentB, "Omar");
        await TransferAsync(f, ticketId, "conv-hops", AgentC, "Sara");

        var history = f.WorkflowEvents.All
            .Where(e => e.EventType is WorkflowEventType.GenesysAgentConnected or WorkflowEventType.GenesysCallTransferred)
            .OrderBy(e => e.TicketWorkflowEventId)
            .ToList();
        Assert.Equal(3, history.Count);
        Assert.Equal([a, b, c], history.Select(e => e.ToEmployeeId!.Value));
        Assert.Equal(a, history[1].ActorEmployeeId);
        Assert.Equal(b, history[2].ActorEmployeeId);
    }

    [Fact]
    public async Task ARedeliveredRoutingEvent_WritesNoSecondTransfer()
    {
        var (f, ticketId, _, _) = await CallAnsweredByAAsync("conv-redeliver");

        await TransferAsync(f, ticketId, "conv-redeliver", AgentB, "Omar");
        await TransferAsync(f, ticketId, "conv-redeliver", AgentB, "Omar");

        Assert.Single(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.GenesysCallTransferred);
    }

    [Fact]
    public async Task ATransferToAnAgentWithNoTigerCsUser_IsStillPreserved_VerbatimInTheNote()
    {
        var (f, ticketId, a, _) = await CallAnsweredByAAsync("conv-unmapped");

        await TransferAsync(f, ticketId, "conv-unmapped", "ga-contractor-9", "Contractor Desk");

        var transfer = Assert.Single(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.GenesysCallTransferred);
        Assert.Equal(a, transfer.ActorEmployeeId);
        Assert.Null(transfer.ToEmployeeId);
        Assert.Contains("ga-contractor-9", transfer.Note);
        Assert.Contains("Contractor Desk", transfer.Note);
        Assert.Contains("not mapped", transfer.Note);
    }

    [Fact]
    public async Task TheFirstTimeGenesysNamesAnAgent_IsNotATransfer()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.MapAgent(AgentA, displayName: "Layla");
        var result = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-late-agent", department.DepartmentId));

        await TransferAsync(f, result.Ticket!.TicketId, "conv-late-agent", AgentA, "Layla");

        Assert.DoesNotContain(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.GenesysCallTransferred);
        Assert.Single(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.GenesysAgentConnected);
    }

    [Fact]
    public async Task CallTransfers_NeverTouchTicketOwnership_OrTheAssignmentHistory()
    {
        var (f, ticketId, _, _) = await CallAnsweredByAAsync("conv-owner");
        var ticket = f.Tickets.All.Single(t => t.TicketId == ticketId);
        var ownerBefore = ticket.CurrentOwnerEmployeeId;
        var assignmentsBefore = f.TicketAssignments.Added.Count;

        await TransferAsync(f, ticketId, "conv-owner", AgentB, "Omar");

        // The approved rule: who handled the call and who owns the ticket are different facts.
        Assert.Equal(ownerBefore, ticket.CurrentOwnerEmployeeId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(assignmentsBefore, f.TicketAssignments.Added.Count);
    }

    [Fact]
    public async Task TheTicketDetail_CountsNoTransferAndNoEscalation_ForGenesysCallTransfers()
    {
        var (f, ticketId, a, _) = await CallAnsweredByAAsync("conv-detail");
        await TransferAsync(f, ticketId, "conv-detail", AgentB, "Omar");
        var query = new TicketQueryAppService(
            f.Tickets, f.DepartmentAssignments, f.Resolutions, f.StatusHistory, ReopenPolicy.Default, TimeProvider.System,
            interactionRepository: f.Interactions, workflowEventRepository: f.WorkflowEvents);

        var detail = (await query.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticketId)).Response!;

        Assert.Equal(0, detail.TransferCount);
        Assert.Equal(0, detail.EscalationCount);
        Assert.Equal(a, detail.FirstHandledByEmployeeId);
        Assert.Null(detail.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task TheAgentContextEndpoint_RecordsTheFirstHandlerInTheHistory_OnlyOnce()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var a = f.MapAgent(AgentA);
        await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-ctx", department.DepartmentId));

        await f.AgentContext.ResolveAsync(ServiceAccount, new GenesysAgentContextDto(AgentA, ConversationId: "conv-ctx"));
        await f.AgentContext.ResolveAsync(ServiceAccount, new GenesysAgentContextDto(AgentA, ConversationId: "conv-ctx"));

        var connected = Assert.Single(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.GenesysAgentConnected);
        Assert.Equal(a, connected.ToEmployeeId);
    }
}
