using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The AI ↔ human agent handoff flow, end to end through the REAL services.
///
/// <para>
/// The load-bearing rule these all orbit: <b>an AI conversation that ends
/// must never orphan its ticket</b>. Whichever way the AI stops — the
/// customer asks for a person, or the connection simply drops — the ticket
/// keeps its id, keeps its transcript, is neither resolved nor closed, and
/// somebody is waiting on it.
/// </para>
/// </summary>
public class AiHumanHandoffFlowTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private static readonly IReadOnlyCollection<string> CsAgent = [Roles.CsAgent];

    private static GenesysTranscriptMessageDto Bot(string body, int minutesAgo) =>
        new("VirtualAgent", DateTime.UtcNow.AddMinutes(-minutesAgo), body, SenderName: "Tiger Bot", SenderId: "bot-1");

    private static GenesysTranscriptMessageDto Customer(string body, int minutesAgo) =>
        new("Customer", DateTime.UtcNow.AddMinutes(-minutesAgo), body);

    private static GenesysTranscriptMessageDto Human(string body, int minutesAgo) =>
        new("HumanAgent", DateTime.UtcNow.AddMinutes(-minutesAgo), body, SenderName: "Layla", SenderId: "ga-7");

    private static async Task<(GenesysServiceFixture Fixture, long TicketId, int DepartmentId)> AiConversationAsync(
        string conversationId = "conv-ai")
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var created = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(
                conversationId, GenesysChannel.WebsiteChat,
                CustomerPhone: "+971500000001", CustomerName: "Ahmed Ali", DepartmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, created.Outcome);
        return (f, created.Ticket!.TicketId, department.DepartmentId);
    }

    // =====================================================================
    // AI disconnect — the safety net
    // =====================================================================

    [Fact]
    public async Task AiConversationEndsWithNoHuman_RaisesWaitingForAgent_WithTheAiConnectionLostTrigger()
    {
        var (f, ticketId, _) = await AiConversationAsync();

        // The terse end event: Genesys says only "the conversation finished".
        // No Handoff block at all.
        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai",
                Ended: new GenesysConversationEndUpdateDto(
                    DateTime.UtcNow, "CustomerDisconnect",
                    [Customer("Hello?", 5), Bot("I can help with that.", 4), Customer("Are you there?", 3)])));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);

        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Equal(AgentHandoffStatus.WaitingForAgent, handoff.Status);
        Assert.Equal(HandoffTrigger.AiConnectionLost, handoff.Trigger);
        Assert.True(handoff.IsOpen);

        // Same ticket, and it is neither resolved nor closed.
        Assert.Equal(ticketId, handoff.TicketId);
        var ticket = await f.Tickets.GetByIdAsync(ticketId);
        Assert.Equal(TicketStatus.Open, ticket!.TicketStatus);
        Assert.Null(ticket.ResolutionOutcome);
    }

    [Fact]
    public async Task AutoRaisedHandoff_WaitsFromTheConversationEnd_NotFromNow()
    {
        var (f, ticketId, _) = await AiConversationAsync();
        var endedAt = DateTime.UtcNow.AddMinutes(-20);

        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai",
                Ended: new GenesysConversationEndUpdateDto(endedAt, "Timeout", [Bot("Still there?", 21)])));

        // The customer has been waiting since the AI dropped them — measuring
        // the Human Wait from "now" would erase 20 minutes of it.
        Assert.Equal(endedAt, Assert.Single(f.Handoffs.All).RequestedAtUtc);
    }

    [Fact]
    public async Task ConversationWithNoVirtualAgentActivity_RaisesNothing()
    {
        var (f, ticketId, _) = await AiConversationAsync();

        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai",
                Ended: new GenesysConversationEndUpdateDto(
                    DateTime.UtcNow, "AgentDisconnect", [Customer("Thanks", 2), Human("You're welcome.", 1)])));

        // An ordinary human conversation ending is not pending work.
        Assert.Empty(f.Handoffs.All);
    }

    [Fact]
    public async Task AiConversationAHumanAlreadyHandled_RaisesNothing()
    {
        var (f, ticketId, _) = await AiConversationAsync();

        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai",
                Ended: new GenesysConversationEndUpdateDto(
                    DateTime.UtcNow, "AgentDisconnect",
                    [Bot("Let me get someone.", 5), Human("Hi, I can take this.", 4)])));

        // The bot handed over and a person spoke. Nothing is pending.
        Assert.Empty(f.Handoffs.All);
    }

    [Fact]
    public async Task GenesysAlreadyAskedForAHuman_TheSafetyNetAddsNothing()
    {
        var (f, ticketId, _) = await AiConversationAsync();

        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai",
                Handoff: new GenesysHandoffUpdateDto(
                    Required: true, Trigger: nameof(HandoffTrigger.CustomerRequestedHuman),
                    Reason: "Customer asked for a person"),
                Ended: new GenesysConversationEndUpdateDto(
                    DateTime.UtcNow, "CustomerDisconnect", [Bot("One moment.", 2)])));

        // Exactly one work item, carrying the trigger GENESYS stated — the
        // safety net must not overwrite it with AiConnectionLost.
        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Equal(HandoffTrigger.CustomerRequestedHuman, handoff.Trigger);
    }

    [Fact]
    public async Task RedeliveredEndEvent_NeverRaisesASecondWorkItem()
    {
        var (f, ticketId, _) = await AiConversationAsync();
        var end = new GenesysConversationEndUpdateDto(
            DateTime.UtcNow, "CustomerDisconnect", [Bot("Hello?", 3)]);

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-ai", Ended: end));
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-ai", Ended: end));

        Assert.Single(f.Handoffs.All);
    }

    [Fact]
    public async Task TranscriptIsPreservedAcrossTheHandoff()
    {
        var (f, ticketId, _) = await AiConversationAsync();

        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai",
                Ended: new GenesysConversationEndUpdateDto(
                    DateTime.UtcNow, "CustomerDisconnect",
                    [Customer("I need an NOC", 6), Bot("Which unit?", 5), Customer("Tower A 1203", 4)])));

        var interaction = await f.Conversations.GetByConversationIdAsync("conv-ai");
        var messages = await f.Conversations.ListMessagesAsync(interaction!.TicketInteractionId);
        Assert.Equal(3, messages.Count);
        Assert.Equal("I need an NOC", messages[0].Body);
        Assert.Equal(InteractionMessageSender.VirtualAgent, messages[1].Sender);
    }

    // =====================================================================
    // Customer explicitly asks for a human
    // =====================================================================

    [Fact]
    public async Task CustomerRequestsHuman_RecordsTheTypedTrigger_AndLeavesTheTicketAlone()
    {
        var (f, ticketId, _) = await AiConversationAsync();

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai",
                Handoff: new GenesysHandoffUpdateDto(
                    Required: true,
                    Reason: "Customer asked to speak to an agent",
                    Trigger: nameof(HandoffTrigger.CustomerRequestedHuman))));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(nameof(AgentHandoffStatus.WaitingForAgent), result.HandoffStatus);

        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Equal(HandoffTrigger.CustomerRequestedHuman, handoff.Trigger);
        Assert.Equal("Customer asked to speak to an agent", handoff.RequestReason);

        // Echoed on every response: proof the ticket did not move.
        Assert.Equal(nameof(TicketStatus.Open), result.TicketStatus);
    }

    [Fact]
    public async Task AnUnrecognizedTrigger_IsRefused_RatherThanCoerced()
    {
        var (f, ticketId, _) = await AiConversationAsync();

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai", Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: "CustomerGotBored")));

        Assert.Equal(GenesysTicketUpdateOutcome.InvalidHandoffTrigger, result.Outcome);
        Assert.Empty(f.Handoffs.All);
    }

    [Fact]
    public async Task AnOmittedTrigger_StaysUnstated_NeverGuessed()
    {
        var (f, ticketId, _) = await AiConversationAsync();

        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-ai", Handoff: new GenesysHandoffUpdateDto(Required: true)));

        Assert.Null(Assert.Single(f.Handoffs.All).Trigger);
    }

    // =====================================================================
    // Stand-down (Required = false) — the AI resumed
    // =====================================================================

    [Fact]
    public async Task RequiredFalse_StandsTheWorkDown_WithoutTouchingTheTicket()
    {
        var (f, ticketId, _) = await AiConversationAsync();
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-ai", Handoff: new GenesysHandoffUpdateDto(Required: true)));

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai",
                Handoff: new GenesysHandoffUpdateDto(Required: false, Reason: "The AI reconnected and resumed.")));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);

        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Equal(AgentHandoffStatus.Cancelled, handoff.Status);
        Assert.False(handoff.IsOpen);
        Assert.Equal("The AI reconnected and resumed.", handoff.ResolutionNote);

        // History is preserved, not deleted — and the ticket never moved.
        Assert.Equal(ticketId, handoff.TicketId);
        Assert.Equal(nameof(TicketStatus.Open), result.TicketStatus);
    }

    [Fact]
    public async Task StandingDownWithoutAReason_IsRefused()
    {
        var (f, ticketId, _) = await AiConversationAsync();
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-ai", Handoff: new GenesysHandoffUpdateDto(Required: true)));

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-ai", Handoff: new GenesysHandoffUpdateDto(Required: false)));

        Assert.Equal(GenesysTicketUpdateOutcome.HandoffReasonRequired, result.Outcome);
        Assert.Equal(AgentHandoffStatus.WaitingForAgent, Assert.Single(f.Handoffs.All).Status);
    }

    [Fact]
    public async Task RepeatedStandDown_IsIdempotent_AndKeepsTheFirstReason()
    {
        var (f, ticketId, _) = await AiConversationAsync();
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-ai", Handoff: new GenesysHandoffUpdateDto(Required: true)));

        var standDown = new GenesysTicketUpdateDto(
            "conv-ai", Handoff: new GenesysHandoffUpdateDto(Required: false, Reason: "First reason"));

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, standDown);
        var second = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-ai", Handoff: new GenesysHandoffUpdateDto(Required: false, Reason: "Second reason")));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, second.Outcome);
        Assert.Equal("First reason", Assert.Single(f.Handoffs.All).ResolutionNote);
    }

    [Fact]
    public async Task AnUpdateCarryingOnlyAnAssignment_DoesNotStandTheWorkDown()
    {
        // The regression this guards: Required is a tri-state precisely so an
        // omitted flag cannot read as a cancellation of the very work the
        // update is reporting an assignment for.
        var (f, ticketId, _) = await AiConversationAsync();
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-ai", Handoff: new GenesysHandoffUpdateDto(Required: true)));

        var assigned = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-ai", Handoff: new GenesysHandoffUpdateDto(AssignedAgentId: "ga-9")));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, assigned.Outcome);
        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Equal(AgentHandoffStatus.Assigned, handoff.Status);
        Assert.True(handoff.IsOpen);
    }

    // =====================================================================
    // First Response — AI never satisfies it, the first human message does
    // =====================================================================

    [Fact]
    public async Task AiMessages_NeverSatisfyFirstResponse()
    {
        var (f, ticketId, _) = await AiConversationAsync();

        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai",
                Ended: new GenesysConversationEndUpdateDto(
                    DateTime.UtcNow, "CustomerDisconnect",
                    [Customer("Hi", 6), Bot("Hello! How can I help?", 5), Bot("Are you there?", 4)])));

        // ISSUE-019: a machine answering in seconds must not satisfy the KPI.
        var ticket = await f.Tickets.GetByIdAsync(ticketId);
        Assert.Null(ticket!.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task TheFirstHumanMessage_SatisfiesFirstResponse_AtItsOwnTimestamp()
    {
        var (f, ticketId, _) = await AiConversationAsync();
        var humanSpokeAt = DateTime.UtcNow.AddMinutes(-4);

        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai",
                Ended: new GenesysConversationEndUpdateDto(
                    DateTime.UtcNow, "AgentDisconnect",
                    [
                        Customer("Hi", 6),
                        Bot("Hello! How can I help?", 5),
                        new GenesysTranscriptMessageDto("HumanAgent", humanSpokeAt, "Layla here, taking over."),
                        new GenesysTranscriptMessageDto("HumanAgent", DateTime.UtcNow.AddMinutes(-2), "All sorted.")
                    ])));

        var ticket = await f.Tickets.GetByIdAsync(ticketId);

        // The FIRST human line, not the last and not the conversation's end.
        Assert.Equal(humanSpokeAt, ticket!.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task FirstResponse_IsWriteOnce_AcrossRedeliveries()
    {
        var (f, ticketId, _) = await AiConversationAsync();
        var humanSpokeAt = DateTime.UtcNow.AddMinutes(-4);
        var end = new GenesysConversationEndUpdateDto(
            DateTime.UtcNow, "AgentDisconnect",
            [new GenesysTranscriptMessageDto("HumanAgent", humanSpokeAt, "Layla here.", ExternalMessageId: "m-1")]);

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-ai", Ended: end));
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-ai", Ended: end));

        var ticket = await f.Tickets.GetByIdAsync(ticketId);
        Assert.Equal(humanSpokeAt, ticket!.FirstHumanResponseAtUtc);
    }

    // =====================================================================
    // Human acceptance — exclusive claim, ownership, Open → InProgress
    // =====================================================================

    private static async Task<(GenesysServiceFixture Fixture, long TicketId, long HandoffId, Guid Agent)>
        WaitingForHumanAsync()
    {
        var (f, ticketId, departmentId) = await AiConversationAsync();
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai",
                Ended: new GenesysConversationEndUpdateDto(
                    DateTime.UtcNow.AddMinutes(-5), "CustomerDisconnect", [Bot("Hello?", 6)])));

        var agent = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(agent, departmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        return (f, ticketId, Assert.Single(f.Handoffs.All).TicketAgentHandoffId, agent);
    }

    [Fact]
    public async Task TicketStaysOpenAndUnowned_WhileWaitingForAHuman()
    {
        var (f, ticketId, _, _) = await WaitingForHumanAsync();

        var ticket = await f.Tickets.GetByIdAsync(ticketId);
        Assert.Equal(TicketStatus.Open, ticket!.TicketStatus);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task HumanAccepts_ClaimsTheWork_TakesTheTicket_AndMovesItToInProgress()
    {
        var (f, ticketId, handoffId, agent) = await WaitingForHumanAsync();

        var result = await f.PendingWork.AcceptAsync(agent, CsAgent, handoffId);

        Assert.Equal(AgentHandoffOutcome.Success, result.Outcome);

        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Equal(AgentHandoffStatus.InProgress, handoff.Status);
        Assert.Equal(agent, handoff.AssignedEmployeeId);
        Assert.NotNull(handoff.StartedAtUtc);

        var ticket = await f.Tickets.GetByIdAsync(ticketId);
        Assert.Equal(TicketStatus.InProgress, ticket!.TicketStatus);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task Accepting_DoesNotRecordFirstHumanResponse()
    {
        // Confirmed decision 10: accepting is not replying. The customer has
        // heard nothing yet.
        var (f, ticketId, handoffId, agent) = await WaitingForHumanAsync();

        await f.PendingWork.AcceptAsync(agent, CsAgent, handoffId);

        var ticket = await f.Tickets.GetByIdAsync(ticketId);
        Assert.Null(ticket!.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task ASecondAgent_IsRefusedWithTheHoldersIdentity()
    {
        var (f, ticketId, handoffId, first) = await WaitingForHumanAsync();
        var second = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(
                second, (await f.Tickets.GetByIdAsync(ticketId))!.CurrentDepartmentId,
                isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));

        Assert.Equal(AgentHandoffOutcome.Success, (await f.PendingWork.AcceptAsync(first, CsAgent, handoffId)).Outcome);

        var loser = await f.PendingWork.AcceptAsync(second, CsAgent, handoffId);

        Assert.Equal(AgentHandoffOutcome.AlreadyClaimed, loser.Outcome);
        Assert.Equal(first, loser.HolderEmployeeId);
        Assert.NotNull(loser.ClaimedAtUtc);

        // And the work still belongs to the winner.
        Assert.Equal(first, Assert.Single(f.Handoffs.All).AssignedEmployeeId);
    }

    [Fact]
    public async Task TheSameAgentRepeating_IsIdempotent()
    {
        var (f, _, handoffId, agent) = await WaitingForHumanAsync();

        Assert.Equal(AgentHandoffOutcome.Success, (await f.PendingWork.AcceptAsync(agent, CsAgent, handoffId)).Outcome);
        var startedAt = Assert.Single(f.Handoffs.All).StartedAtUtc;

        var again = await f.PendingWork.AcceptAsync(agent, CsAgent, handoffId);

        Assert.Equal(AgentHandoffOutcome.Success, again.Outcome);
        Assert.Equal(startedAt, Assert.Single(f.Handoffs.All).StartedAtUtc);
    }

    [Fact]
    public async Task AcceptingWorkInADepartmentTheCallerCannotSee_IsForbidden()
    {
        var (f, _, handoffId, _) = await WaitingForHumanAsync();
        var outsider = Guid.NewGuid();

        // A Department Employee with no membership of the ticket's department.
        var result = await f.PendingWork.AcceptAsync(outsider, [Roles.DepartmentEmployee], handoffId);

        Assert.Equal(AgentHandoffOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task SystemAdministrator_AcceptsAcrossDepartmentsAndWithoutOwnership()
    {
        var (f, _, handoffId, _) = await WaitingForHumanAsync();
        var administrator = Guid.NewGuid();

        // No department membership at all — ADR-0024's override carries it.
        var result = await f.PendingWork.AcceptAsync(administrator, [Roles.SystemAdministrator], handoffId);

        Assert.Equal(AgentHandoffOutcome.Success, result.Outcome);
        Assert.Equal(administrator, Assert.Single(f.Handoffs.All).AssignedEmployeeId);
    }

    [Fact]
    public async Task ResolutionSlaKeepsRunningThroughoutTheWait()
    {
        var (f, ticketId, handoffId, agent) = await WaitingForHumanAsync();

        var beforeAccept = await f.Tickets.GetByIdAsync(ticketId);
        var slaStateWhileWaiting = beforeAccept!.SlaState;

        await f.PendingWork.AcceptAsync(agent, CsAgent, handoffId);

        var afterAccept = await f.Tickets.GetByIdAsync(ticketId);

        // Nothing about a handoff pauses, stops or resets the Resolution
        // clock: the customer is still waiting for their case to be done.
        Assert.Equal(slaStateWhileWaiting, afterAccept!.SlaState);
        Assert.Null(afterAccept.ResolutionOutcome);
        Assert.NotEqual(TicketStatus.Resolved, afterAccept.TicketStatus);
        Assert.NotEqual(TicketStatus.Closed, afterAccept.TicketStatus);
    }

    [Fact]
    public async Task AcceptingNeverResolvesOrClosesTheTicket_NorChangesItsId()
    {
        var (f, ticketId, handoffId, agent) = await WaitingForHumanAsync();

        await f.PendingWork.AcceptAsync(agent, CsAgent, handoffId);
        await f.PendingWork.CompleteAsync(agent, CsAgent, handoffId, new CompleteAgentHandoffRequestDto("Spoke to the customer."));

        var ticket = await f.Tickets.GetByIdAsync(ticketId);
        Assert.Equal(ticketId, ticket!.TicketId);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Null(ticket.ResolutionOutcome);
        Assert.Equal(AgentHandoffStatus.Completed, Assert.Single(f.Handoffs.All).Status);
    }

    // =====================================================================
    // Ticket Activity
    // =====================================================================

    [Fact]
    public async Task TheHandoffStoryReachesTicketActivity()
    {
        var (f, ticketId, handoffId, agent) = await WaitingForHumanAsync();
        await f.PendingWork.AcceptAsync(agent, CsAgent, handoffId);
        await f.PendingWork.CompleteAsync(agent, CsAgent, handoffId, new CompleteAgentHandoffRequestDto("Done."));

        var events = await f.WorkflowEvents.ListByTicketIdAsync(ticketId);
        var types = events.Select(e => e.EventType).ToList();

        Assert.Contains(WorkflowEventType.HandoffRequested, types);
        Assert.Contains(WorkflowEventType.HandoffStarted, types);
        Assert.Contains(WorkflowEventType.HandoffCompleted, types);
    }

    // =====================================================================
    // Dashboard
    // =====================================================================

    [Fact]
    public async Task AwaitingHumanAgentSnapshot_CountsWaitingWork_AndRespectsDepartmentVisibility()
    {
        var (f, ticketId, _) = await AiConversationAsync();
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-ai",
                Ended: new GenesysConversationEndUpdateDto(
                    DateTime.UtcNow.AddMinutes(-10), "CustomerDisconnect", [Bot("Hello?", 11)])));

        var handoff = Assert.Single(f.Handoffs.All);

        var visible = await f.Handoffs.GetAwaitingHumanAgentSnapshotAsync([handoff.DepartmentId]);
        Assert.Equal(1, visible.Count);
        Assert.Equal(handoff.RequestedAtUtc, visible.OldestRequestedAtUtc);

        // A department the caller cannot see contributes nothing.
        var invisible = await f.Handoffs.GetAwaitingHumanAgentSnapshotAsync([handoff.DepartmentId + 999]);
        Assert.Equal(0, invisible.Count);
        Assert.Null(invisible.OldestRequestedAtUtc);
    }

    [Fact]
    public async Task ClaimedWork_LeavesTheAwaitingHumanAgentQueue()
    {
        var (f, _, handoffId, agent) = await WaitingForHumanAsync();
        var departmentId = Assert.Single(f.Handoffs.All).DepartmentId;

        Assert.Equal(1, (await f.Handoffs.GetAwaitingHumanAgentSnapshotAsync([departmentId])).Count);

        await f.PendingWork.AcceptAsync(agent, CsAgent, handoffId);

        // It is still open work, but nobody is waiting on it any more.
        Assert.Equal(0, (await f.Handoffs.GetAwaitingHumanAgentSnapshotAsync([departmentId])).Count);
    }
}
