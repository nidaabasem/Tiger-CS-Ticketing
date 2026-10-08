using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Seed;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// The SLA explanation states what the implementation does, in the order it
/// decides it. These tests pin the three "why is there no SLA" reasons and
/// the policy/clock facts for a running period.
/// </summary>
public class SlaExplanationTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);

    private static SlaPolicy Policy(PriorityLevel level) =>
        SlaReferenceData.Policies().Single(p => p.PriorityId == (byte)level);

    private static SlaCalendarDto Calendar() =>
        new("Default", "08:00", "18:00", "Asia/Dubai", ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Saturday"], 0, []);

    [Fact]
    public void UnclassifiedGenesysTicket_HasNoPeriod_AndSaysClassificationAndPriorityAreRequired()
    {
        // The shape GenesysInquiryIngestionAppService creates: no category, no priority.
        var ticket = Ticket.CreateUnclassified("TG-CS-20261007-0001", departmentId: 1, "Website chat started via Genesys", Now);

        var explanation = SlaQueryAppService.BuildExplanation(ticket, instance: null, policy: null, calendar: null);

        Assert.False(explanation.HasActivePeriod);
        Assert.Null(explanation.ClockStartedAtUtc);
        Assert.Null(explanation.AppliedPriorityLabel);
        Assert.Contains("unclassified", explanation.NotStartedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("classifies the ticket and sets its priority", explanation.NotStartedReason, StringComparison.Ordinal);
        Assert.Contains("not at the time of the interaction", explanation.NotStartedReason, StringComparison.Ordinal);
        // Start Handling / Accept & Start is never presented as a first response.
        Assert.Contains("Accept & Start / Start Handling", explanation.FirstResponseRule, StringComparison.Ordinal);
        Assert.Contains("do NOT count", explanation.FirstResponseRule, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifiedTicketWithoutAPolicy_SaysWhichPriorityHasNoPolicy()
    {
        var ticket = Ticket.CreateVerified("TG-CS-20261007-0002", 1, 10, 20, categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC", Now);

        var explanation = SlaQueryAppService.BuildExplanation(ticket, instance: null, policy: null, calendar: null);

        Assert.Contains("no active SLA policy is configured for priority High", explanation.NotStartedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningBusinessHoursPeriod_NamesThePolicyTargetsClockAndCalendar()
    {
        var ticket = Ticket.CreateVerified("TG-CS-20261007-0003", 1, 10, 20, categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC", Now);
        var policy = Policy(PriorityLevel.High);
        var instance = TicketSlaInstance.OpenInitialPeriod(
            ticket.TicketId, policy.PriorityId, Now, Now.AddMinutes(policy.FirstResponseTargetMinutes),
            Now.AddMinutes(policy.ResolutionTargetMinutes));

        var explanation = SlaQueryAppService.BuildExplanation(ticket, instance, policy, Calendar());

        Assert.True(explanation.HasActivePeriod);
        Assert.Null(explanation.NotStartedReason);
        Assert.Equal("High", explanation.AppliedPriorityLabel);
        Assert.Equal(SlaQueryAppService.PolicySourcePriority, explanation.PolicySource);
        Assert.Equal(policy.FirstResponseTargetMinutes, explanation.FirstResponseTargetMinutes);
        Assert.Equal(policy.ResolutionTargetMinutes, explanation.ResolutionTargetMinutes);
        Assert.Equal("Business hours", explanation.ClockBasis);
        Assert.Equal(Now, explanation.ClockStartedAtUtc);
        Assert.Equal("InitialCreation", explanation.PeriodReason);
        Assert.NotNull(explanation.Calendar);
        Assert.Contains(explanation.Notes, n => n.Contains("08:00–18:00 (Asia/Dubai)", StringComparison.Ordinal));
        Assert.Contains("pauses while a non-Critical ticket is Pending Customer", explanation.PauseRule, StringComparison.Ordinal);
        Assert.DoesNotContain("not implemented", explanation.PauseRule, StringComparison.Ordinal);
    }

    [Fact]
    public void CriticalPeriod_IsExplainedAsTwentyFourSeven()
    {
        var ticket = Ticket.CreateVerified("TG-CS-20261007-0004", 1, 10, 20, categoryId: 5, priorityId: (byte)PriorityLevel.Critical, "Flood", Now);
        var policy = Policy(PriorityLevel.Critical);
        var instance = TicketSlaInstance.OpenInitialPeriod(ticket.TicketId, policy.PriorityId, Now, Now.AddMinutes(15), Now.AddHours(4));

        var explanation = SlaQueryAppService.BuildExplanation(ticket, instance, policy, calendar: null);

        Assert.Equal("24/7", explanation.ClockBasis);
        Assert.Null(explanation.Calendar);
        Assert.Contains(explanation.Notes, n => n.StartsWith("24/7 clock", StringComparison.Ordinal));
    }
}
