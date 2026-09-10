using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.GenesysIntegration.Domain;

/// <summary>
/// Domain invariants added by the Genesys integration phase: an
/// interaction's conversation lifecycle, the transcript message, and the
/// routing configuration entities.
/// </summary>
public class GenesysDomainTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 9, 32, 0, DateTimeKind.Utc);

    private static TicketInteraction GenesysInteraction(string conversationId = "conv-1") =>
        TicketInteraction.CreateFromGenesys(
            ticketId: 1, WellKnownChannels.LiveChat, "+971500000001",
            genesysConversationId: conversationId, calledNumber: null,
            genesysQueueId: null, genesysQueueName: null, genesysAgentId: null, genesysAgentName: null,
            interactionStartedAtUtc: Now, direction: "Inbound", Now,
            customerName: "Ahmed Ali", customerEmail: "ahmed@example.test");

    // ---- The interaction's own lifecycle ----

    [Fact]
    public void NewInteraction_IsNotEnded()
    {
        var interaction = GenesysInteraction();

        Assert.False(interaction.IsEnded);
        Assert.Null(interaction.EndedAtUtc);
        Assert.Null(interaction.EndReason);
        Assert.Equal("Ahmed Ali", interaction.CustomerName);
        Assert.Equal("ahmed@example.test", interaction.CustomerEmail);
    }

    [Fact]
    public void End_IsWriteOnce_SoARedeliveredEndEventCannotMoveTheRecordedTime()
    {
        var interaction = GenesysInteraction();
        var endedAt = Now.AddMinutes(15);

        interaction.End(endedAt, "AgentDisconnect");

        Assert.True(interaction.IsEnded);
        Assert.Equal(endedAt, interaction.EndedAtUtc);

        var second = Assert.Throws<TicketInteractionAlreadyEndedException>(
            () => interaction.End(endedAt.AddHours(1), "CustomerDisconnect"));
        Assert.Equal(endedAt, second.EndedAtUtc);
        // Unchanged by the refused second call.
        Assert.Equal(endedAt, interaction.EndedAtUtc);
        Assert.Equal("AgentDisconnect", interaction.EndReason);
    }

    [Fact]
    public void End_WithoutAReason_IsAllowed_BecauseAnInterruptedChatOftenHasNone()
    {
        var interaction = GenesysInteraction();

        interaction.End(Now.AddMinutes(4), endReason: null);

        Assert.True(interaction.IsEnded);
        Assert.Null(interaction.EndReason);
    }

    [Fact]
    public void RecordAgentIfAbsent_FillsInAMissingAgent_ButNeverOverwritesAKnownOne()
    {
        var withoutAgent = GenesysInteraction();
        withoutAgent.RecordAgentIfAbsent("ga-7", "John");
        Assert.Equal("ga-7", withoutAgent.GenesysAgentId);
        Assert.Equal("John", withoutAgent.GenesysAgentName);

        var withAgent = TicketInteraction.CreateFromGenesys(
            1, WellKnownChannels.Phone, "+971500000001", "conv-2", null, null, null,
            genesysAgentId: "ga-1", genesysAgentName: "Layla", null, null, Now);
        withAgent.RecordAgentIfAbsent("ga-9", "Someone Else");
        Assert.Equal("ga-1", withAgent.GenesysAgentId);
        Assert.Equal("Layla", withAgent.GenesysAgentName);
    }

    [Fact]
    public void LocalInteraction_HasNoConversationAndCannotBeConfusedWithAGenesysOne()
    {
        var local = TicketInteraction.CreateLocal(1, WellKnownChannels.FaceToFaceKiosk, null, Now);

        Assert.Equal(InteractionContextSource.Ticketing, local.Source);
        Assert.Null(local.GenesysConversationId);
        Assert.Null(local.CustomerName);
        Assert.False(local.IsEnded);
    }

    // ---- Transcript messages ----

    [Fact]
    public void Message_RequiresABody_AndAOneBasedSequence()
    {
        Assert.Throws<ArgumentException>(() =>
            new TicketInteractionMessage(1, 1, InteractionMessageSender.Customer, null, null, Now, "  "));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TicketInteractionMessage(1, 0, InteractionMessageSender.Customer, null, null, Now, "Hello"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TicketInteractionMessage(1, 1, (InteractionMessageSender)99, null, null, Now, "Hello"));
    }

    [Fact]
    public void Message_KeepsItsBodyVerbatim_AndTrimsOnlyTheIdentityFields()
    {
        var message = new TicketInteractionMessage(
            1, 1, InteractionMessageSender.Agent, "  John  ", "  ga-7  ", Now, "  Thank you for waiting.  ", "  msg-1  ");

        // The transcript body is evidence — never trimmed or normalized.
        Assert.Equal("  Thank you for waiting.  ", message.Body);
        Assert.Equal("John", message.SenderName);
        Assert.Equal("ga-7", message.SenderId);
        Assert.Equal("msg-1", message.ExternalMessageId);
    }

    // ---- Routing configuration ----

    [Fact]
    public void QueueMapping_RequiresAQueueIdAndARealDepartment()
    {
        Assert.Throws<ArgumentException>(() => new GenesysQueueMapping("  ", null, 1, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenesysQueueMapping("q-1", null, 0, Now));

        var mapping = new GenesysQueueMapping("  q-1  ", "  CS Queue  ", 5, Now);
        Assert.Equal("q-1", mapping.QueueId);
        Assert.Equal("CS Queue", mapping.QueueName);
        Assert.True(mapping.IsActive);
    }

    [Fact]
    public void QueueMapping_Update_RePointsAndDeactivates_ButNeverChangesTheQueueId()
    {
        var mapping = new GenesysQueueMapping("q-1", "CS Queue", 5, Now);

        mapping.Update("Leasing Queue", 9, isActive: false, Now.AddDays(1));

        Assert.Equal("q-1", mapping.QueueId);
        Assert.Equal(9, mapping.DepartmentId);
        Assert.False(mapping.IsActive);
        Assert.Equal(Now.AddDays(1), mapping.UpdatedAtUtc);
        Assert.Equal(Now, mapping.CreatedAtUtc);
    }

    // ---- Pending human work (any channel) ----

    private static TicketAgentHandoff Handoff(
        AgentHandoffMode? mode = null, string? genesysAgentId = null, string? workItemId = null) =>
        new(ticketId: 1, ticketInteractionId: 2, departmentId: 5, channelId: WellKnownChannels.LiveChat,
            requestedAtUtc: Now, createdAtUtc: Now, mode: mode, requestReason: "Bot could not answer",
            externalWorkItemId: workItemId, assignedEmployeeId: null, genesysAgentId: genesysAgentId,
            assignedAtUtc: null);

    [Fact]
    public void NewHandoff_WithNobodyAvailable_WaitsForAnAgent()
    {
        var handoff = Handoff();

        Assert.Equal(AgentHandoffStatus.WaitingForAgent, handoff.Status);
        Assert.True(handoff.IsOpen);
        Assert.True(handoff.RequiresHumanAgent);
        Assert.Null(handoff.AssignedEmployeeId);
        Assert.Null(handoff.AssignedAtUtc);
        Assert.Null(handoff.ResolvedAtUtc);

        // Nothing about how it continues is guessed from the channel.
        Assert.Null(handoff.Mode);
    }

    [Fact]
    public void NewHandoff_WithAnAgentAlreadyNamed_IsAssignedFromTheStart()
    {
        var handoff = Handoff(genesysAgentId: "ga-7");

        Assert.Equal(AgentHandoffStatus.Assigned, handoff.Status);
        Assert.Equal("ga-7", handoff.GenesysAgentId);
        Assert.Equal(Now, handoff.AssignedAtUtc);
    }

    [Fact]
    public void NewHandoff_RequiresARealDepartmentAndChannel_AndARecognizedMode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TicketAgentHandoff(1, 2, departmentId: 0, WellKnownChannels.Phone, Now, Now));
        Assert.Throws<ArgumentException>(() =>
            new TicketAgentHandoff(1, 2, 5, channelId: 0, Now, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TicketAgentHandoff(1, 2, 5, WellKnownChannels.Phone, Now, Now, mode: (AgentHandoffMode)99));
    }

    [Theory]
    [InlineData(AgentHandoffMode.Callback)]
    [InlineData(AgentHandoffMode.ContinueChat)]
    [InlineData(AgentHandoffMode.ReplyInChannel)]
    [InlineData(AgentHandoffMode.HumanTakeover)]
    public void EveryFollowUpMode_IsStorableOnAnyChannel(AgentHandoffMode mode)
    {
        // The entity never ties a mode to a channel: which one applies where
        // is Genesys' behaviour to state, and it has not stated it.
        Assert.Equal(mode, Handoff(mode).Mode);
    }

    [Fact]
    public void AssignTo_IsIdempotent_SoARedeliveredAssignmentDoesNotMoveTheRecordedTime()
    {
        var handoff = Handoff();
        var assignedAt = Now.AddMinutes(5);

        handoff.AssignTo(employeeId: null, genesysAgentId: "ga-9", assignedAt);
        Assert.Equal(AgentHandoffStatus.Assigned, handoff.Status);
        Assert.Equal(assignedAt, handoff.AssignedAtUtc);

        handoff.AssignTo(employeeId: null, genesysAgentId: "ga-9", assignedAt.AddHours(1));
        Assert.Equal(assignedAt, handoff.AssignedAtUtc);
    }

    [Fact]
    public void AssignTo_MustNameSomeone()
    {
        var handoff = Handoff();

        Assert.Throws<ArgumentException>(() => handoff.AssignTo(employeeId: null, genesysAgentId: "  ", Now));
    }

    [Fact]
    public void Start_TakesTheWork_AndNeverRewindsOnAReassignment()
    {
        var handoff = Handoff();
        var agent = Guid.NewGuid();
        var startedAt = Now.AddMinutes(10);

        handoff.Start(agent, startedAt);
        Assert.Equal(AgentHandoffStatus.InProgress, handoff.Status);
        // Starting is also taking it — there is no separate claim step.
        Assert.Equal(agent, handoff.AssignedEmployeeId);
        Assert.Equal(startedAt, handoff.StartedAtUtc);

        // A real mid-handling reassignment keeps it in progress.
        handoff.AssignTo(Guid.NewGuid(), genesysAgentId: null, startedAt.AddMinutes(1));
        Assert.Equal(AgentHandoffStatus.InProgress, handoff.Status);

        // And starting again does not move the start time.
        handoff.Start(agent, startedAt.AddHours(1));
        Assert.Equal(startedAt, handoff.StartedAtUtc);
    }

    [Fact]
    public void Complete_ResolvesTheWork_AndRefusesEverythingAfterwards()
    {
        var handoff = Handoff();
        var agent = Guid.NewGuid();
        var completedAt = Now.AddMinutes(30);

        handoff.Complete(agent, completedAt, "Answered in the chat thread.");

        Assert.Equal(AgentHandoffStatus.Completed, handoff.Status);
        Assert.Equal(completedAt, handoff.CompletedAtUtc);
        Assert.Equal(completedAt, handoff.ResolvedAtUtc);
        Assert.False(handoff.IsOpen);

        Assert.Throws<AgentHandoffAlreadyResolvedException>(() => handoff.Complete(agent, completedAt, null));
        Assert.Throws<AgentHandoffAlreadyResolvedException>(() => handoff.Cancel(completedAt, "Too late"));
        Assert.Throws<AgentHandoffAlreadyResolvedException>(() => handoff.Start(agent, completedAt));
        Assert.Throws<AgentHandoffAlreadyResolvedException>(() => handoff.AssignTo(agent, null, completedAt));
    }

    [Fact]
    public void Cancel_RequiresAReason_AndResolvesWithoutCompleting()
    {
        var handoff = Handoff();

        Assert.Throws<ArgumentException>(() => handoff.Cancel(Now, "   "));
        Assert.True(handoff.IsOpen);

        handoff.Cancel(Now.AddMinutes(2), "Customer answered themselves.");

        Assert.Equal(AgentHandoffStatus.Cancelled, handoff.Status);
        Assert.False(handoff.IsOpen);
        // Cancelled is resolved but never completed — the distinction the
        // work list and any later reporting depend on.
        Assert.Null(handoff.CompletedAtUtc);
        Assert.Equal("Customer answered themselves.", handoff.ResolutionNote);
    }
}
