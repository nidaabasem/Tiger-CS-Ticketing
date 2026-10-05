using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Domain;

public class CollectionsReminderDomainTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);

    private static CollectionsReminder Reminder(ReminderChannel channel = ReminderChannel.VoiceBot) =>
        new("9001", "ACC-1", "9200", ReminderType.CurrentMonthDue, channel, "2026-10", "AED", 10_000m, false, Now,
            ReminderTrigger.Integration, Guid.NewGuid(), Now);

    [Fact]
    public void DeduplicationKey_IsAccountTypeCycleChannel()
    {
        Assert.Equal("ACC-1|CurrentMonthDue|2026-10|VoiceBot", Reminder().DeduplicationKey);
        Assert.NotEqual(Reminder(ReminderChannel.Sms).DeduplicationKey, Reminder().DeduplicationKey);
    }

    [Fact]
    public void AReminderIsOnlyRecordedForAnAmountOwed() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CollectionsReminder("9001", "ACC-1", null, ReminderType.Manual, ReminderChannel.Email, "2026-10-15", "AED", 0m, false, Now,
                ReminderTrigger.Manual, null, Now));

    [Fact]
    public void QueuedSentDelivered_MoveForwardOnly()
    {
        var reminder = Reminder();
        Assert.Equal(ReminderStatus.Queued, reminder.Status);

        Assert.True(reminder.ApplyDelivery(ReminderEventType.Sent, Now.AddMinutes(1), null, "call-1"));
        Assert.True(reminder.ApplyDelivery(ReminderEventType.Delivered, Now.AddMinutes(2), null, null));
        Assert.Equal(ReminderStatus.Delivered, reminder.Status);

        // A late Sent or Failed never undoes Delivered.
        Assert.False(reminder.ApplyDelivery(ReminderEventType.Sent, Now.AddMinutes(3), null, null));
        Assert.False(reminder.ApplyDelivery(ReminderEventType.Failed, Now.AddMinutes(3), "late", null));
        Assert.Equal(ReminderStatus.Delivered, reminder.Status);
        Assert.Equal(Now.AddMinutes(1), reminder.SentAtUtc);
        Assert.Equal("call-1", reminder.ProviderReference);
    }

    [Fact]
    public void ASuppressedReminderAcceptsNoDelivery()
    {
        var reminder = Reminder();
        Assert.True(reminder.Suppress("Settled before dispatch.", Now));
        Assert.False(reminder.ApplyDelivery(ReminderEventType.Delivered, Now, null, null));
        Assert.Equal(ReminderStatus.Suppressed, reminder.Status);
        Assert.False(reminder.Suppress("again", Now));
    }

    [Theory]
    [InlineData(CustomerResponseKind.AlreadyPaid, true, true)]
    [InlineData(CustomerResponseKind.RequestedHuman, false, true)]
    [InlineData(CustomerResponseKind.AiDisconnected, false, true)]
    [InlineData(CustomerResponseKind.Disputed, false, true)]
    [InlineData(CustomerResponseKind.PromiseToPay, false, false)]
    public void ResponseKinds_RaiseTheRightFollowUp(CustomerResponseKind kind, bool verification, bool human)
    {
        var reminder = Reminder();
        var response = CollectionsReminderEvent.Response(reminder, "evt-1", kind, Now, Now, null, "conv-1", "+971500000900", null, null, null);

        Assert.Equal(verification, response.VerificationFollowUpRequired);
        Assert.Equal(human, response.HumanFollowUpRequired);
        Assert.Equal(ResponseTicketStatus.Pending, response.TicketStatus);
    }

    [Fact]
    public void AResponseOutsideAConversation_OwesNoTicket()
    {
        var response = CollectionsReminderEvent.Response(Reminder(), "evt-1", CustomerResponseKind.PromiseToPay, Now, Now, null,
            conversationId: null, customerPhone: null, note: "Reply by SMS", new DateOnly(2026, 10, 20), 5_000m);

        Assert.Equal(ResponseTicketStatus.NotApplicable, response.TicketStatus);
        Assert.Throws<InvalidOperationException>(() => response.LinkTicket(1, "T-1", Now));
    }

    [Fact]
    public void LinkTicket_IsIdempotentForTheSameTicket_AndRefusesAnother()
    {
        var response = CollectionsReminderEvent.Response(Reminder(), "evt-1", CustomerResponseKind.AlreadyPaid, Now, Now, null, "conv-1", null, null, null, null);

        response.LinkTicket(10, "TG-1", Now);
        response.LinkTicket(10, "TG-1", Now.AddMinutes(5));
        Assert.Equal(ResponseTicketStatus.Linked, response.TicketStatus);
        Assert.Equal(Now, response.TicketLinkedAtUtc);
        Assert.Throws<InvalidOperationException>(() => response.LinkTicket(11, "TG-2", Now));
    }

    [Fact]
    public void CallerEventIds_MayNotImpersonateTigerCsEvents() =>
        Assert.Throws<ArgumentException>(() =>
            CollectionsReminderEvent.Delivery(Reminder(), "tigercs:sent", ReminderEventType.Sent, Now, Now, null, null));

    [Fact]
    public void ThereIsNoLegalReminderType() =>
        Assert.DoesNotContain(Enum.GetNames<ReminderType>(), n => n.Contains("Legal", StringComparison.OrdinalIgnoreCase));
}
