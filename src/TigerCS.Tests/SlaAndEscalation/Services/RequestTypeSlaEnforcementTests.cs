using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.SlaAndEscalation.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// Request-type SLA enforcement (RequestTypeSlaPolicy per request type +
/// priority): only the unambiguous subset changes due dates; everything else
/// falls back to the per-priority policy and says exactly why. Calendar:
/// Sat–Thu 08:00–18:00 Asia/Dubai (UTC+4). Created Sun 2026-08-23 05:00Z
/// (09:00 local).
/// </summary>
public class RequestTypeSlaEnforcementTests
{
    private const int RequestTypeId = 7;
    private static readonly DateTime CreatedAt = new(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);

    private static DateTime Utc(int day, int hour, int minute = 0) => new(2026, 8, day, hour, minute, 0, DateTimeKind.Utc);

    private static RequestTypeSlaPolicy Policy(
        PriorityLevel priority = PriorityLevel.Medium,
        int? resolutionTarget = 1,
        int? resolutionMax = null,
        SlaDurationUnit unit = SlaDurationUnit.Days,
        SlaClockBasis? clock = SlaClockBasis.BusinessHours,
        SlaTriggerType trigger = SlaTriggerType.TicketCreated,
        bool isImmediate = false,
        int? firstResponse = null,
        bool? pausesOnPendingCustomer = null) =>
        new(RequestTypeId, (byte)priority, trigger, unit, firstResponse, null,
            isImmediate ? null : resolutionTarget, isImmediate ? null : resolutionMax,
            isImmediate, clock, pausesOnPendingCustomer, null, null, true);

    private static async Task<(SlaServiceFixture Sla, Ticket Ticket)> SetUpAsync(
        RequestTypeSlaPolicy? policy, PriorityLevel priority = PriorityLevel.Medium, bool withRequestType = true)
    {
        var sla = new SlaServiceFixture();
        if (policy is not null)
        {
            await sla.RequestTypeSla.AddAsync(policy);
        }

        var ticket = Ticket.CreateVerified("TG-CS-20260823-0001", 2, 10, 20, 5, (byte)priority, "x", CreatedAt);
        if (withRequestType)
        {
            ticket.ClassifyRequestType(RequestTypeId);
        }

        await sla.Tickets.AddAsync(ticket);
        return (sla, ticket);
    }

    private static Task<TicketSlaInstance> OpenAsync(SlaServiceFixture sla, Ticket ticket) =>
        sla.DueDates.OpenInitialPeriodAsync(ticket, CreatedAt, actorEmployeeId: null, Guid.NewGuid());

    // ---- enforced ----

    [Fact]
    public async Task SingleBusinessDay_OnAnExplicitBusinessClock_OverridesThePriorityResolution()
    {
        var (sla, ticket) = await SetUpAsync(Policy(resolutionTarget: 1));

        var instance = await OpenAsync(sla, ticket);

        // 1 business day = 10h of business time: 9h Sunday, then Monday 08:00-09:00 local.
        Assert.Equal(Utc(24, 5), instance.ResolutionDueAtUtc);
        Assert.NotNull(instance.RequestTypeSlaPolicyId);
        Assert.Equal(SlaClockBasis.BusinessHours, instance.ResolutionClockBasis);
        Assert.Null(instance.RequestTypeSlaNote);
        Assert.Equal(600, instance.AppliedResolutionTargetMinutes);

        // First Response is null in the request-type row, so the priority policy (Medium: 4h) stays.
        Assert.Equal(240, instance.AppliedFirstResponseTargetMinutes);
        var (priorityFirstResponse, priorityResolution) = await sla.DueDates.ComputeDueDatesAsync((byte)PriorityLevel.Medium, CreatedAt);
        Assert.Equal(priorityFirstResponse, instance.FirstResponseDueAtUtc);
        Assert.NotEqual(priorityResolution, instance.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task DaysOnACalendarClock_AreTwentyFourHoursEach()
    {
        var (sla, ticket) = await SetUpAsync(Policy(resolutionTarget: 2, clock: SlaClockBasis.TwentyFourSeven));

        var instance = await OpenAsync(sla, ticket);

        Assert.Equal(CreatedAt.AddHours(48), instance.ResolutionDueAtUtc);
        Assert.Equal(SlaClockBasis.TwentyFourSeven, instance.ResolutionClockBasis);
    }

    [Fact]
    public async Task HoursOnABusinessClock_CountBusinessHoursOnly()
    {
        var (sla, ticket) = await SetUpAsync(Policy(resolutionTarget: 12, unit: SlaDurationUnit.Hours));

        var instance = await OpenAsync(sla, ticket);

        // 9h left Sunday + 3h Monday -> Monday 11:00 local = 07:00Z.
        Assert.Equal(Utc(24, 7), instance.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task Minutes_AreHonoured()
    {
        var (sla, ticket) = await SetUpAsync(Policy(resolutionTarget: 90, unit: SlaDurationUnit.Minutes, clock: SlaClockBasis.TwentyFourSeven));

        var instance = await OpenAsync(sla, ticket);

        Assert.Equal(CreatedAt.AddMinutes(90), instance.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task EqualBounds_AreASingleValue()
    {
        var (sla, ticket) = await SetUpAsync(Policy(resolutionTarget: 1, resolutionMax: 1));

        var instance = await OpenAsync(sla, ticket);

        Assert.NotNull(instance.RequestTypeSlaPolicyId);
        Assert.Equal(Utc(24, 5), instance.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task Immediate_MakesTheResolutionDueTheClockStart()
    {
        var (sla, ticket) = await SetUpAsync(Policy(isImmediate: true, clock: SlaClockBasis.TwentyFourSeven));

        var instance = await OpenAsync(sla, ticket);

        Assert.Equal(CreatedAt, instance.ResolutionDueAtUtc);
        Assert.Equal(0, instance.AppliedResolutionTargetMinutes);
        Assert.NotNull(instance.RequestTypeSlaPolicyId);
    }

    [Fact]
    public async Task ASingleRequestTypeFirstResponse_IsUsed()
    {
        var (sla, ticket) = await SetUpAsync(Policy(unit: SlaDurationUnit.Hours, resolutionTarget: 12, firstResponse: 2));

        var instance = await OpenAsync(sla, ticket);

        Assert.Equal(Utc(23, 7), instance.FirstResponseDueAtUtc);
        Assert.Equal(120, instance.AppliedFirstResponseTargetMinutes);
    }

    [Fact]
    public async Task TheAuditEntry_RecordsWhichLayerDecidedTheTargets()
    {
        var (sla, ticket) = await SetUpAsync(Policy(resolutionTarget: 1));

        await OpenAsync(sla, ticket);

        var audit = Assert.Single(sla.Audit.Entries, e => e.Action == "ComputeSlaDueDates");
        Assert.Contains("\"policySource\":\"RequestType\"", audit.AfterValue);
        Assert.Contains("\"resolutionClockBasis\":\"BusinessHours\"", audit.AfterValue);
    }

    // ---- NOT enforced: fall back to the priority policy and say why ----

    private static async Task AssertFallsBackAsync(RequestTypeSlaPolicy policy, params string[] reasonFragments)
    {
        var (sla, ticket) = await SetUpAsync(policy);
        var (expectedFirstResponse, expectedResolution) = await sla.DueDates.ComputeDueDatesAsync((byte)PriorityLevel.Medium, CreatedAt);

        var instance = await OpenAsync(sla, ticket);

        // Exactly the per-priority policy...
        Assert.Equal(expectedFirstResponse, instance.FirstResponseDueAtUtc);
        Assert.Equal(expectedResolution, instance.ResolutionDueAtUtc);
        Assert.Null(instance.RequestTypeSlaPolicyId);

        // ...and the exact reason is kept on the period and surfaced in the explanation.
        Assert.NotNull(instance.RequestTypeSlaNote);
        Assert.StartsWith("Request-type SLA not applied:", instance.RequestTypeSlaNote, StringComparison.Ordinal);
        foreach (var fragment in reasonFragments)
        {
            Assert.Contains(fragment, instance.RequestTypeSlaNote, StringComparison.Ordinal);
        }

        var explanation = SlaQueryAppService.BuildExplanation(ticket, instance, SlaReferencePolicy(PriorityLevel.Medium), calendar: null);
        Assert.False(explanation.RequestTypeSlaApplied);
        Assert.Equal(instance.RequestTypeSlaNote, explanation.RequestTypeSlaNote);
        Assert.Contains(instance.RequestTypeSlaNote!, explanation.Notes);
        Assert.Equal(SlaQueryAppService.PolicySourcePriority, explanation.PolicySource);

        var audit = Assert.Single(sla.Audit.Entries, e => e.Action == "ComputeSlaDueDates");
        Assert.Contains("\"policySource\":\"Priority\"", audit.AfterValue);
        Assert.Contains("Request-type SLA not applied", audit.AfterValue);
    }

    private static SlaPolicy SlaReferencePolicy(PriorityLevel priority) =>
        TigerCS.Infrastructure.Modules.SlaAndEscalation.Seed.SlaReferenceData.Policies().Single(p => p.PriorityId == (byte)priority);

    [Fact]
    public Task Range_IsNotEnforced_AndSaysTheRangeInterpretationIsUndecided() =>
        AssertFallsBackAsync(Policy(resolutionTarget: 10, resolutionMax: 12), "range", "10–12 Days", "undecided");

    [Fact]
    public Task NullClockBasis_IsNotEnforced_AndSaysBusinessVsCalendarDaysIsUndecided() =>
        AssertFallsBackAsync(Policy(clock: null), "business days vs. calendar days", "weekend and holiday", "undecided");

    [Theory]
    [InlineData(SlaTriggerType.ApprovalReceived)]
    [InlineData(SlaTriggerType.CustomerServiceApproved)]
    [InlineData(SlaTriggerType.PrerequisitesCompleted)]
    [InlineData(SlaTriggerType.Assigned)]
    public Task NonCreationTrigger_IsNotEnforced_AndNamesTheTrigger(SlaTriggerType trigger) =>
        AssertFallsBackAsync(Policy(trigger: trigger), trigger.ToString(), "not at ticket creation");

    [Fact]
    public async Task NoRequestTypeOnTheTicket_OrNoRow_LeavesNoNoteAtAll()
    {
        var (slaA, ticketA) = await SetUpAsync(Policy(), withRequestType: false);
        var (slaB, ticketB) = await SetUpAsync(policy: null);

        var withoutRequestType = await OpenAsync(slaA, ticketA);
        var withoutRow = await OpenAsync(slaB, ticketB);

        Assert.Null(withoutRequestType.RequestTypeSlaNote);
        Assert.Null(withoutRequestType.RequestTypeSlaPolicyId);
        Assert.Null(withoutRow.RequestTypeSlaNote);
        Assert.Equal(withoutRow.ResolutionDueAtUtc, withoutRequestType.ResolutionDueAtUtc);
    }

    // ---- the explanation when it IS enforced ----

    [Fact]
    public async Task TheExplanation_SaysTheRequestTypeSlaApplied()
    {
        var (sla, ticket) = await SetUpAsync(Policy(resolutionTarget: 1));
        var instance = await OpenAsync(sla, ticket);

        var explanation = SlaQueryAppService.BuildExplanation(ticket, instance, SlaReferencePolicy(PriorityLevel.Medium), calendar: null);

        Assert.True(explanation.RequestTypeSlaApplied);
        Assert.Null(explanation.RequestTypeSlaNote);
        Assert.Equal(SlaQueryAppService.PolicySourceRequestType, explanation.PolicySource);
        Assert.Equal(600, explanation.ResolutionTargetMinutes);
        Assert.Equal(240, explanation.FirstResponseTargetMinutes);
        Assert.Contains(explanation.Notes, n => n.StartsWith("Request-type SLA applied", StringComparison.Ordinal));
    }

    // ---- per-request-type pause override is snapshotted on the period ----

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThePauseOverride_IsSnapshottedOnThePeriod(bool? flag)
    {
        var (sla, ticket) = await SetUpAsync(Policy(pausesOnPendingCustomer: flag));

        var instance = await OpenAsync(sla, ticket);

        Assert.Equal(flag, instance.PausesOnPendingCustomerOverride);
    }

    [Fact]
    public async Task ThePauseOverride_IsHonoured_EvenWhenTheDurationsAreNotEnforced()
    {
        var (sla, ticket) = await SetUpAsync(Policy(resolutionTarget: 10, resolutionMax: 12, pausesOnPendingCustomer: false));

        var instance = await OpenAsync(sla, ticket);

        Assert.Null(instance.RequestTypeSlaPolicyId);
        Assert.False(instance.PausesOnPendingCustomerOverride);
    }

    // ---- reopen carries the same rules ----

    [Fact]
    public async Task AReopenCycle_RecomputesThroughTheSameEnforcement()
    {
        var (sla, ticket) = await SetUpAsync(Policy(resolutionTarget: 1));
        await OpenAsync(sla, ticket);
        var reopenedAt = CreatedAt.AddDays(3);

        var cycle = await sla.DueDates.StartReopenResolutionCycleAsync(ticket, reopenedAt, null, Guid.NewGuid());

        Assert.NotNull(cycle);
        Assert.NotNull(cycle.RequestTypeSlaPolicyId);
        Assert.Equal(SlaClockBasis.BusinessHours, cycle.ResolutionClockBasis);
        Assert.True(cycle.ResolutionDueAtUtc > reopenedAt);
    }
}
