using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Chatbot customer-inactivity closure: the timer's lifecycle (start, repeat,
/// cancel, restart), the timeout job's re-checks (human assignment, requested
/// handoff, bot connection failure, ended conversation), the reply/timeout
/// race, and the closure itself (lifecycle path, Cancelled — not Resolved —
/// outcome, audit reason, single notification, idempotent re-runs).
///
/// <para>
/// These run the REAL ingestion, update facade and lifecycle services over
/// fakes with a controllable clock; "restart" is a second closer instance over
/// the same stores, because the whole point is that the timer lives in the
/// persisted interaction row and not in the process.
/// </para>
/// </summary>
public class ChatbotInactivityCloseTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string Reason5 = "Automatically closed — customer did not respond for more than 5 minutes.";

    private static async Task<(GenesysServiceFixture F, long TicketId, string ConversationId)> IngestChatAsync(
        string conversationId = "conv-bot", int? timeoutMinutes = null)
    {
        var f = new GenesysServiceFixture(inactivityTimeoutMinutes: timeoutMinutes);
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(
                conversationId, GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: department.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return (f, result.Ticket!.TicketId, conversationId);
    }

    private static Task<GenesysTicketUpdateResult> SetAwaitingAsync(
        GenesysServiceFixture f, long ticketId, string conversationId, bool awaiting) =>
        f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId, new GenesysTicketUpdateDto(conversationId, AwaitingCustomerReply: awaiting));

    private static async Task<ChatbotInactivityCloseOutcome> RunJobAsync(GenesysServiceFixture f)
    {
        // The job body: list candidates, then re-check and close each.
        var outcome = ChatbotInactivityCloseOutcome.NotDue;
        foreach (var id in await f.InactivityClose.ListDueAsync())
        {
            outcome = await f.InactivityClose.TryCloseAsync(id);
        }

        return outcome;
    }

    // ---- timer lifecycle ----

    [Fact]
    public async Task ChatbotPrompt_StartsTheTimer_AndReportsTheDeadline()
    {
        var (f, ticketId, conv) = await IngestChatAsync();

        var result = await SetAwaitingAsync(f, ticketId, conv, true);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.True(result.AwaitingCustomerReply);
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime.AddMinutes(5), result.InactivityDeadlineUtc);
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime, f.InteractionFor(conv)!.AwaitingCustomerReplySinceUtc);
        Assert.Contains(f.Audit.Entries, e => e.Action == GenesysAuditActions.CustomerReplyTimerStarted);
    }

    [Fact]
    public async Task RepeatedWebhookDelivery_NeverRestartsTheTimer()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);
        var started = f.InteractionFor(conv)!.AwaitingCustomerReplySinceUtc;

        f.Clock.Advance(TimeSpan.FromMinutes(3));
        var repeat = await SetAwaitingAsync(f, ticketId, conv, true);

        Assert.Equal(started, f.InteractionFor(conv)!.AwaitingCustomerReplySinceUtc);
        Assert.Equal(started!.Value.AddMinutes(5), repeat.InactivityDeadlineUtc);
        // One start audit entry, not two.
        Assert.Single(f.Audit.Entries, e => e.Action == GenesysAuditActions.CustomerReplyTimerStarted);

        // The deadline therefore still falls 5 minutes after the FIRST prompt.
        f.Clock.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await RunJobAsync(f));
    }

    [Fact]
    public async Task CustomerReply_CancelsTheTimer_AndANewPromptStartsANewOne()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);

        f.Clock.Advance(TimeSpan.FromMinutes(4));
        var replied = await SetAwaitingAsync(f, ticketId, conv, false);
        Assert.False(replied.AwaitingCustomerReply);
        Assert.Null(f.InteractionFor(conv)!.AwaitingCustomerReplySinceUtc);

        // 10 minutes of silence later nothing is due — there is no timer.
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f));
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);

        // The chatbot asks again: a fresh timer, measured from now.
        var again = await SetAwaitingAsync(f, ticketId, conv, true);
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime, f.InteractionFor(conv)!.AwaitingCustomerReplySinceUtc);
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime.AddMinutes(5), again.InactivityDeadlineUtc);
    }

    [Fact]
    public async Task OmittedField_LeavesARunningTimerAlone()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, AgentName: "Bot"));

        Assert.NotNull(f.InteractionFor(conv)!.AwaitingCustomerReplySinceUtc);
    }

    // ---- timeout ----

    [Fact]
    public async Task Timeout_IsStrictlyMoreThanFiveMinutes_ThenClosesThroughTheLifecycle()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);
        var ticket = f.Tickets.All.Single();

        f.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f)); // exactly 5:00 is not "more than"
        Assert.NotEqual(TicketStatus.Closed, ticket.TicketStatus);

        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await RunJobAsync(f));

        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);

        // Closed as Cancelled — never as "Resolved", which would claim the issue was confirmed fixed.
        Assert.Equal((byte)ResolutionOutcome.Cancelled, ticket.ResolutionOutcome);
        var resolution = Assert.Single(f.Resolutions.Added);
        Assert.Equal(ResolutionOutcome.Cancelled, resolution.ResolutionOutcome);
        Assert.Equal(Reason5, resolution.ResolutionNote);
        Assert.Equal(ServiceAccount, resolution.ResolvingEmployeeId);

        // The lifecycle's required Resolved step happened, as a system action, with the reason.
        var statusRows = f.StatusHistory.Added
            .Where(h => h.TicketId == ticketId && h.Dimension == TicketStatusDimension.TicketStatus && h.ActorIsSystem)
            .ToList();
        Assert.Contains(statusRows, h => h.NewValue == (byte)TicketStatus.Resolved && h.Note == Reason5);
        Assert.Contains(statusRows, h => h.OldValue == (byte)TicketStatus.Resolved && h.NewValue == (byte)TicketStatus.Closed && h.Note == Reason5);

        // Audit entry carries the exact required reason.
        var audit = Assert.Single(f.Audit.Entries, e => e.Action == "AutoCloseCustomerInactivity");
        Assert.Equal("Ticket", audit.EntityType);
        Assert.Equal(ticketId.ToString(), audit.EntityId);
        Assert.Contains(Reason5, audit.AfterValue);

        // The timer is spent and the closure is recorded on the interaction.
        var interaction = f.InteractionFor(conv)!;
        Assert.Null(interaction.AwaitingCustomerReplySinceUtc);
        Assert.NotNull(interaction.InactivityClosedAtUtc);
    }

    [Fact]
    public async Task RepeatedTimeoutProcessing_ClosesOnce_AndNotifiesOnce()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);
        f.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await RunJobAsync(f));
        var interactionId = f.InteractionFor(conv)!.TicketInteractionId;

        // Overlapping / retried runs, including a direct re-attempt on the same interaction.
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await f.InactivityClose.TryCloseAsync(interactionId));
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f));

        Assert.Single(f.Resolutions.Added);
        Assert.Single(f.Audit.Entries, e => e.Action == "AutoCloseCustomerInactivity");

        // Exactly one customer lifecycle message — Closed. No "resolved" email: nobody resolved anything.
        var lifecycleMessages = f.Outbox.Committed.Where(m => m.EventType.StartsWith("Ticket", StringComparison.Ordinal)).ToList();
        Assert.Single(lifecycleMessages, m => m.EventType == "TicketClosed");
        Assert.DoesNotContain(lifecycleMessages, m => m.EventType == "TicketResolved");
    }

    [Fact]
    public async Task TimeoutSurvivesARestart_BecauseTheTimerIsPersisted()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);
        f.Clock.Advance(TimeSpan.FromMinutes(6));

        // A brand-new closer over the same stores — as after an API restart or deploy.
        var restarted = new ChatbotInactivityCloseAppService(
            f.Options, f.Conversations, f.Tickets, f.Handoffs, f.Lifecycle, f.UnitOfWork, f.Clock);

        var due = await restarted.ListDueAsync();
        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await restarted.TryCloseAsync(Assert.Single(due)));
        Assert.Equal(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task TimeoutIsConfigurable_AndTheReasonNamesTheConfiguredMinutes()
    {
        var (f, ticketId, conv) = await IngestChatAsync(timeoutMinutes: 10);
        await SetAwaitingAsync(f, ticketId, conv, true);

        f.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f));

        f.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await RunJobAsync(f));
        Assert.Equal(
            "Automatically closed — customer did not respond for more than 10 minutes.",
            Assert.Single(f.Resolutions.Added).ResolutionNote);
    }

    [Fact]
    public async Task ZeroTimeout_SwitchesAutomaticClosureOff()
    {
        var (f, ticketId, conv) = await IngestChatAsync(timeoutMinutes: 0);
        await SetAwaitingAsync(f, ticketId, conv, true);
        f.Clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f));
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    // ---- exclusions: the job re-checks at fire time ----

    [Fact]
    public async Task HumanAssignedAfterThePrompt_IsNeverClosed_AndTheTimerIsDropped()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);

        var ticket = f.Tickets.All.Single();
        ticket.AssignTo(Guid.NewGuid());
        f.Clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(ChatbotInactivityCloseOutcome.Excluded, await RunJobAsync(f));

        Assert.NotEqual(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Empty(f.Resolutions.Added);
        Assert.Null(f.InteractionFor(conv)!.AwaitingCustomerReplySinceUtc);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoCloseCustomerInactivity");
    }

    [Fact]
    public async Task RequestedHumanHandoff_IsNeverClosed()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);

        // The bot raises a handoff without telling the timer (a second integration path).
        await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto(conv, AgentAvailable: false, Reason: "Customer asked for a person"));
        f.Clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(ChatbotInactivityCloseOutcome.Excluded, await RunJobAsync(f));
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
        Assert.Empty(f.Resolutions.Added);
    }

    [Fact]
    public async Task HandoffRequestedInTheSameUpdate_CancelsTheTimerImmediately()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                conv, Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: "CustomerRequestedHuman")));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.False(result.AwaitingCustomerReply);
        Assert.Null(f.InteractionFor(conv)!.AwaitingCustomerReplySinceUtc);
    }

    [Fact]
    public async Task TimerIsNotStarted_WhileHumanFollowUpIsPending()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto(conv, AgentAvailable: false, Reason: "Bot could not answer"));

        var result = await SetAwaitingAsync(f, ticketId, conv, true);

        Assert.False(result.AwaitingCustomerReply);
        Assert.Contains("human follow-up", result.AwaitingCustomerReplyNote, StringComparison.OrdinalIgnoreCase);
        Assert.Null(f.InteractionFor(conv)!.AwaitingCustomerReplySinceUtc);
    }

    [Fact]
    public async Task BotConnectionFailure_ConversationEndsWithoutAHuman_NeverAutoCloses()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);

        // The AI connection drops: the conversation ends, and TigerCS raises AiConnectionLost human work itself.
        var end = await f.ConversationEnd.EndAsync(
            ServiceAccount, new GenesysConversationEndDto(conv, null, "Disconnected", null, null, null));
        Assert.Equal(GenesysConversationEndOutcome.Ended, end.Outcome);

        f.Clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Null(f.InteractionFor(conv)!.AwaitingCustomerReplySinceUtc);
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f));
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
        Assert.Empty(f.Resolutions.Added);
    }

    [Fact]
    public async Task EndedConversation_IsNotAnActiveChatbotInteraction()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await f.ConversationEnd.EndAsync(
            ServiceAccount, new GenesysConversationEndDto(conv, null, "CustomerDisconnect", null, null, null));

        var result = await SetAwaitingAsync(f, ticketId, conv, true);

        Assert.False(result.AwaitingCustomerReply);
        Assert.Contains("ended", result.AwaitingCustomerReplyNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TicketAlreadyClosedByAPerson_IsLeftAlone()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);

        var ticket = f.Tickets.All.Single();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, null);
        ticket.Close();
        f.Clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(ChatbotInactivityCloseOutcome.Excluded, await RunJobAsync(f));
        Assert.Empty(f.Resolutions.Added);
        Assert.Null(f.InteractionFor(conv)!.AwaitingCustomerReplySinceUtc);
    }

    // ---- reply / timeout race ----

    [Fact]
    public async Task ReplyArrivingBeforeTheJobReadsTheInteraction_WinsTheRace()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);
        f.Clock.Advance(TimeSpan.FromMinutes(6));

        // The job has listed its candidate…
        var candidate = Assert.Single(await f.InactivityClose.ListDueAsync());

        // …the customer replies before it re-reads the row…
        await SetAwaitingAsync(f, ticketId, conv, false);

        // …so the re-check finds nothing due and nothing is closed.
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await f.InactivityClose.TryCloseAsync(candidate));
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
        Assert.Empty(f.Resolutions.Added);
    }

    [Fact]
    public async Task ReplyCommittingDuringTheClosure_MakesTheClosureLose_AndNothingIsReported()
    {
        var (f, ticketId, conv) = await IngestChatAsync();
        await SetAwaitingAsync(f, ticketId, conv, true);
        f.Clock.Advance(TimeSpan.FromMinutes(6));
        var candidate = Assert.Single(await f.InactivityClose.ListDueAsync());

        // The database turns the reply's concurrent clearing of the timer
        // (an EF concurrency token) into a failed closure write — modelled
        // here as the unit of work's concurrency failure, which the real one
        // raises for exactly this case (see InteractionTimerConcurrencyTests).
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = f.UnitOfWork.SaveChangesCallCount + 1;
        var committedBefore = f.UnitOfWork.TransactionsCommitted;

        Assert.Equal(ChatbotInactivityCloseOutcome.LostRace, await f.InactivityClose.TryCloseAsync(candidate));

        // The closure's transaction never committed, so no customer message was published.
        Assert.Equal(committedBefore, f.UnitOfWork.TransactionsCommitted);
        Assert.DoesNotContain(f.Outbox.Committed, m => m.EventType == "TicketClosed");
    }

    // ---- domain ----

    [Fact]
    public void BeginAwaiting_IsIdempotent_AndRefusedForAnEndedInteraction()
    {
        var t0 = new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);
        var interaction = TicketInteraction.CreateFromGenesys(
            1, 3, "+971500000001", "c-1", null, null, null, null, null, null, null, t0);

        Assert.True(interaction.BeginAwaitingCustomerReply(t0, ServiceAccount));
        Assert.False(interaction.BeginAwaitingCustomerReply(t0.AddMinutes(2), ServiceAccount));
        Assert.Equal(t0, interaction.AwaitingCustomerReplySinceUtc);

        Assert.False(interaction.IsInactivityTimeoutDue(t0.AddMinutes(5), TimeSpan.FromMinutes(5)));
        Assert.True(interaction.IsInactivityTimeoutDue(t0.AddMinutes(5).AddTicks(1), TimeSpan.FromMinutes(5)));

        interaction.End(t0.AddMinutes(1), "CustomerDisconnect");
        Assert.Null(interaction.AwaitingCustomerReplySinceUtc);
        Assert.False(interaction.BeginAwaitingCustomerReply(t0.AddMinutes(3), ServiceAccount));
    }

    [Fact]
    public void CloseForCustomerInactivity_RefusesAnOwnedTicket_AndAClosedOne()
    {
        var now = DateTime.UtcNow;
        var owned = Ticket.CreateVerified("TG-CS-20261007-0001", 2, 10, 20, 5, 2, "x", now);
        owned.AssignTo(Guid.NewGuid());
        Assert.Throws<TicketNotEligibleForResolutionException>(() => owned.CloseForCustomerInactivity());

        var open = Ticket.CreateVerified("TG-CS-20261007-0002", 2, 10, 20, 5, 2, "x", now);
        Assert.Equal(TicketStatus.Open, open.CloseForCustomerInactivity());
        Assert.Equal(TicketStatus.Closed, open.TicketStatus);
        Assert.Throws<TicketClosedException>(() => open.CloseForCustomerInactivity());
    }
}
