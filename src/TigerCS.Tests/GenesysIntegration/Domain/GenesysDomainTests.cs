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
}
