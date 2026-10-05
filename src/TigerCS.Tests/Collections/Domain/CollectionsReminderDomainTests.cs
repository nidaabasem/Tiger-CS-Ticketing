using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Domain;

public class CollectionsReminderDomainTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 6, 0, 0, DateTimeKind.Utc);

    private static readonly ReminderEligibility Eligible =
        new(true, 30_000m, "AED", "UnpaidPrincipalOlderThanOneCalendarMonth", ["INST-JUN-2026", "INST-JUL-2026"], new DateOnly(2026, 6, 1), null);

    private static CollectionsReminder Job(params ReminderChannel[] channels) =>
        new(12345, "ACC-45001", 45001, ReminderType.OverdueMonthly, "2026-10:OverdueMonthly", Eligible, Now, "en",
            ReminderTrigger.Integration, Guid.NewGuid(), "key-1", "HASH", channels.Length == 0 ? [ReminderChannel.VoiceBot, ReminderChannel.Sms] : channels, Now);

    [Fact]
    public void EachChannel_CarriesItsOwnDeduplicationKey()
    {
        var job = Job(ReminderChannel.VoiceBot, ReminderChannel.Sms, ReminderChannel.Email);

        Assert.Equal(
            ["ACC-45001|OverdueMonthly|2026-10:OverdueMonthly|VoiceBot", "ACC-45001|OverdueMonthly|2026-10:OverdueMonthly|Sms", "ACC-45001|OverdueMonthly|2026-10:OverdueMonthly|Email"],
            job.Channels.Select(c => c.DeduplicationKey));
        Assert.All(job.Channels, c => Assert.Equal(ChannelStatus.Queued, c.Status));
        Assert.Equal("INST-JUN-2026,INST-JUL-2026", job.InstalmentIds);
    }

    [Fact]
    public void OnlyAnEligibleAmountIsEverQueued() =>
        Assert.Throws<ArgumentException>(() => new CollectionsReminder(12345, "ACC-1", null, ReminderType.CurrentMonth, "2026-10:CurrentMonth",
            ReminderEligibility.NotEligible("AED", ReminderPolicy.SettledReason), Now, "en", ReminderTrigger.User, null, null, null, [ReminderChannel.Sms], Now));

    [Fact]
    public void PublicIds_RoundTrip()
    {
        Assert.True(CollectionsReminder.TryParsePublicId("REM-90001", out var id));
        Assert.Equal(90001, id);
        Assert.True(CollectionsReminder.TryParsePublicId("90001", out _));
        Assert.False(CollectionsReminder.TryParsePublicId("REM-x", out _));
        Assert.False(CollectionsReminder.TryParsePublicId("REM--4", out _));
    }

    [Fact]
    public void ADelayedEvent_NeverOverwritesALaterStatus()
    {
        var sms = Job().ChannelFor(ReminderChannel.Sms)!;

        Assert.True(sms.Apply(ChannelStatus.Sent, Now.AddMinutes(1), "m-1", null));
        Assert.True(sms.Apply(ChannelStatus.Failed, Now.AddMinutes(5), null, "bounced"));
        Assert.False(sms.Apply(ChannelStatus.Sent, Now.AddMinutes(2), null, null)); // older than the last applied
        Assert.Equal(ChannelStatus.Failed, sms.Status);
        Assert.Equal("bounced", sms.StatusReason);
    }

    [Fact]
    public void DeliveredOrAnswered_IsFinal()
    {
        var job = Job();
        var voice = job.ChannelFor(ReminderChannel.VoiceBot)!;
        Assert.True(voice.Apply(ChannelStatus.Answered, Now.AddMinutes(1), "call-1", null));
        Assert.False(voice.Apply(ChannelStatus.Failed, Now.AddMinutes(9), null, "late"));
        Assert.Equal(ChannelStatus.Answered, voice.Status);

        var sms = job.ChannelFor(ReminderChannel.Sms)!;
        sms.Apply(ChannelStatus.Delivered, Now.AddMinutes(1), null, null);
        Assert.False(sms.Apply(ChannelStatus.Sent, Now.AddMinutes(2), null, null));
    }

    [Fact]
    public void AChannelRejectsAnotherChannelsVocabulary() =>
        Assert.Throws<ArgumentException>(() => Job().ChannelFor(ReminderChannel.VoiceBot)!.Apply(ChannelStatus.Delivered, Now, null, null));

    [Fact]
    public void OnlyAFailedChannelIsRetried_AndASuppressedOneAcceptsNothing()
    {
        var job = Job();
        var voice = job.ChannelFor(ReminderChannel.VoiceBot)!;
        Assert.Throws<InvalidOperationException>(() => voice.Requeue(Now, null));

        voice.Apply(ChannelStatus.NoAnswer, Now.AddMinutes(1), null, "no answer");
        voice.Requeue(Now.AddHours(1), null);
        Assert.Equal(ChannelStatus.Queued, voice.Status);
        Assert.Equal(2, voice.Attempts);

        var sms = job.ChannelFor(ReminderChannel.Sms)!;
        Assert.True(sms.Suppress("Settled before dispatch.", Now));
        Assert.False(sms.Apply(ChannelStatus.Delivered, Now.AddMinutes(1), null, null));
        Assert.Contains(job.Events, e => e.ExternalEventId == "tigercs:suppressed:Sms:1");
    }

    [Theory]
    [InlineData(CustomerIntent.AlreadyPaid, false, true, true)]
    [InlineData(CustomerIntent.RequestedHuman, false, false, true)]
    [InlineData(CustomerIntent.AiDisconnected, false, false, true)]
    [InlineData(CustomerIntent.Disputed, false, false, true)]
    [InlineData(CustomerIntent.PromiseToPay, false, false, false)]
    [InlineData(CustomerIntent.PromiseToPay, true, false, true)]
    public void Intents_RaiseTheRightFollowUp(CustomerIntent intent, bool callerRequestsHuman, bool verification, bool human)
    {
        var e = CollectionsReminderEvent.Reported(Job(), "EVT-1", null, "H", ReminderChannel.VoiceBot, ChannelStatus.Answered, "GEN-1",
            "conv-1", Now, Now, null, customerResponded: true, intent, callerRequestsHuman, null);

        Assert.Equal(verification, e.VerificationFollowUpRequired);
        Assert.Equal(human, e.RequiresHumanFollowUp);
        Assert.Equal(TicketResult.Pending, e.TicketResult);
    }

    [Fact]
    public void AnAnsweredCallAlone_IsNotAResponse_AndOwesNoTicket()
    {
        var e = CollectionsReminderEvent.Reported(Job(), "EVT-1", null, "H", ReminderChannel.VoiceBot, ChannelStatus.Answered, null,
            "conv-1", Now, Now, null, customerResponded: false, null, requiresHumanFollowUp: true, null);

        Assert.False(e.CustomerResponded);
        Assert.False(e.FollowUpRequired);
        Assert.Equal(TicketResult.NotRequired, e.TicketResult);
    }

    [Fact]
    public void LinkTicket_RecordsCreatedOrReused_IsIdempotent_AndRefusesAnotherTicket()
    {
        var e = CollectionsReminderEvent.Reported(Job(), "EVT-1", null, "H", ReminderChannel.VoiceBot, null, null,
            "conv-1", Now, Now, null, true, CustomerIntent.AlreadyPaid, false, null);

        e.LinkTicket(10, "TG-1", created: false, Now);
        e.LinkTicket(10, "TG-1", created: true, Now.AddMinutes(5));
        Assert.Equal(TicketResult.Reused, e.TicketResult);
        Assert.Equal(Now, e.TicketLinkedAtUtc);
        Assert.Throws<InvalidOperationException>(() => e.LinkTicket(11, "TG-2", true, Now));
    }

    [Fact]
    public void CallerEventIds_MayNotUseTheReservedPrefix() =>
        Assert.Throws<ArgumentException>(() => CollectionsReminderEvent.Reported(Job(), "tigercs:queued:Sms:1", null, "H", ReminderChannel.Sms,
            ChannelStatus.Sent, null, null, Now, Now, null, false, null, false, null));

    [Fact]
    public void ThereIsNoLegalReminderType() =>
        Assert.DoesNotContain(Enum.GetNames<ReminderType>(), n => n.Contains("Legal", StringComparison.OrdinalIgnoreCase));
}
