using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// Request-type SLA precedence at runtime, on fixed instants. Deliberately
/// run on an explicitly configured calendar that is NOT the reference seed —
/// UTC, Monday–Friday, 09:00–17:00 — so nothing here can pass on an assumed
/// window length or work week: every deadline is walked on whatever calendar
/// is configured. Business-DAY rows are not applied at all; what a business
/// day means is an open business decision.
/// </summary>
public class RequestTypeSlaPrecedenceTests
{
    // Friday 2026-10-02 15:00 UTC: two business hours are left on Friday of
    // the configured calendar; Saturday and Sunday are not working days.
    private static readonly DateTime FridayAfternoonUtc = new(2026, 10, 2, 15, 0, 0, DateTimeKind.Utc);

    private static BusinessCalendarSnapshot ConfiguredCalendar(params DateOnly[] holidays) => new(
        TimeZoneInfo.Utc, new TimeOnly(9, 0), new TimeOnly(17, 0),
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
        holidays);

    private static DateTime Utc(int month, int day, int hour) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    private sealed class Setup
    {
        public SlaServiceFixture Sla { get; } = new();
        public FakeRequestTypeRepository RequestTypes { get; } = new();
        public FakeRequestTypeSlaPolicyRepository RequestTypeSlas { get; } = new();
        public SlaDueDateService Service { get; }
        public RequestType RequestType { get; }

        public Setup(bool enforced, Func<int, RequestTypeSlaPolicy>? rowFor = null, BusinessCalendarSnapshot? calendar = null)
        {
            Sla.Calendar.Use(calendar ?? ConfiguredCalendar());
            RequestType = RequestTypes.Add(new RequestType(1, "Synthetic", 1, (byte)PriorityLevel.Medium, false, true, false, true));
            if (enforced)
            {
                RequestType.EnableConfigurationEnforcement();
            }

            if (rowFor?.Invoke(RequestType.RequestTypeId) is { } row)
            {
                RequestTypeSlas.AddAsync(row).GetAwaiter().GetResult();
            }

            Service = new SlaDueDateService(
                Sla.Policies, Sla.Calendar, Sla.SlaInstances, Sla.Scheduler, Sla.Audit, RequestTypes, RequestTypeSlas);
        }
    }

    /// <summary>2 business hours first response, 12 business hours resolution.</summary>
    private static RequestTypeSlaPolicy HoursRow(int requestTypeId) =>
        new(requestTypeId, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Hours,
            firstResponseTargetValue: 2, firstResponseMaximumValue: null, resolutionTargetValue: 12, resolutionMaximumValue: null,
            clockBasis: SlaClockBasis.BusinessHours);

    /// <summary>The workbook's shape: 4 business hours / 1 business day.</summary>
    private static RequestTypeSlaPolicy BusinessDayRow(int requestTypeId) =>
        new(requestTypeId, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days,
            firstResponseTargetValue: 4, firstResponseMaximumValue: null, resolutionTargetValue: 1, resolutionMaximumValue: null,
            clockBasis: SlaClockBasis.BusinessHours, firstResponseUnit: SlaDurationUnit.Hours);

    [Fact]
    public async Task An_enforced_types_row_is_walked_on_the_configured_calendar()
    {
        var setup = new Setup(enforced: true, rowFor: HoursRow);

        var due = await setup.Service.ComputeAsync((byte)PriorityLevel.Medium, setup.RequestType.RequestTypeId, FridayAfternoonUtc);

        // 2h: exactly Friday's remaining window. 12h: Friday 2h + Monday 8h + Tuesday 2h.
        Assert.Equal(Utc(10, 2, 17), due.FirstResponseDueAtUtc);
        Assert.Equal(Utc(10, 6, 11), due.ResolutionDueAtUtc);
        Assert.StartsWith("RequestTypeSla:", due.FirstResponseSource, StringComparison.Ordinal);
        Assert.StartsWith("RequestTypeSla:", due.ResolutionSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_row_follows_a_different_configured_calendar_and_its_holidays()
    {
        var seeded = new Setup(enforced: true, rowFor: HoursRow, calendar: SlaTestCalendar.Default());
        var withHoliday = new Setup(enforced: true, rowFor: HoursRow, calendar: ConfiguredCalendar(new DateOnly(2026, 10, 5)));

        var seededDue = await seeded.Service.ComputeAsync((byte)PriorityLevel.Medium, seeded.RequestType.RequestTypeId, FridayAfternoonUtc);
        var holidayDue = await withHoliday.Service.ComputeAsync((byte)PriorityLevel.Medium, withHoliday.RequestType.RequestTypeId, FridayAfternoonUtc);

        // Seeded calendar (Asia/Dubai, Saturday–Thursday, 08:00–18:00): Friday 19:00 local is off → Saturday 08:00 + 2h.
        Assert.Equal(Utc(10, 3, 6), seededDue.FirstResponseDueAtUtc);
        // Monday 5 October a holiday: Friday 2h, Tuesday 8h, Wednesday 2h.
        Assert.Equal(Utc(10, 7, 11), holidayDue.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task A_business_day_row_is_not_applied_its_meaning_is_an_open_decision()
    {
        var setup = new Setup(enforced: true, rowFor: BusinessDayRow);
        var legacy = new Setup(enforced: false);

        var due = await setup.Service.ComputeAsync((byte)PriorityLevel.Medium, setup.RequestType.RequestTypeId, FridayAfternoonUtc);
        var policy = await legacy.Service.ComputeDueDatesAsync((byte)PriorityLevel.Medium, FridayAfternoonUtc);

        Assert.Equal(("PriorityPolicy", "PriorityPolicy"), (due.FirstResponseSource, due.ResolutionSource));
        Assert.Equal((policy.FirstResponseDueAtUtc, policy.ResolutionDueAtUtc), (due.FirstResponseDueAtUtc, due.ResolutionDueAtUtc));
    }

    [Fact]
    public async Task A_24_7_row_counts_a_day_as_24_hours()
    {
        var setup = new Setup(enforced: true, rowFor: id => new RequestTypeSlaPolicy(
            id, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 4, null, 1, null,
            clockBasis: SlaClockBasis.TwentyFourSeven, firstResponseUnit: SlaDurationUnit.Hours));

        var due = await setup.Service.ComputeAsync((byte)PriorityLevel.Medium, setup.RequestType.RequestTypeId, FridayAfternoonUtc);

        Assert.Equal(FridayAfternoonUtc.AddHours(4), due.FirstResponseDueAtUtc);
        Assert.Equal(FridayAfternoonUtc.AddDays(1), due.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task Without_enforcement_the_same_row_is_ignored_and_the_priority_policy_applies_exactly_as_before()
    {
        var enforcedOff = new Setup(enforced: false, rowFor: HoursRow);
        var noRequestType = new Setup(enforced: false);

        var withRow = await enforcedOff.Service.ComputeAsync((byte)PriorityLevel.Medium, enforcedOff.RequestType.RequestTypeId, FridayAfternoonUtc);
        var legacy = await noRequestType.Service.ComputeDueDatesAsync((byte)PriorityLevel.Medium, FridayAfternoonUtc);

        Assert.Equal((legacy.FirstResponseDueAtUtc, legacy.ResolutionDueAtUtc), (withRow.FirstResponseDueAtUtc, withRow.ResolutionDueAtUtc));
        Assert.Equal(("PriorityPolicy", "PriorityPolicy"), (withRow.FirstResponseSource, withRow.ResolutionSource));
    }

    [Fact]
    public async Task A_deadline_the_row_leaves_empty_or_another_priority_falls_back_to_the_priority_policy()
    {
        var resolutionOnly = new Setup(enforced: true, rowFor: id => new RequestTypeSlaPolicy(
            id, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Hours, null, null, 2, null,
            clockBasis: SlaClockBasis.BusinessHours));

        var due = await resolutionOnly.Service.ComputeAsync((byte)PriorityLevel.Medium, resolutionOnly.RequestType.RequestTypeId, FridayAfternoonUtc);
        var high = await resolutionOnly.Service.ComputeAsync((byte)PriorityLevel.High, resolutionOnly.RequestType.RequestTypeId, FridayAfternoonUtc);

        Assert.Equal("PriorityPolicy", due.FirstResponseSource);
        Assert.Equal(Utc(10, 2, 17), due.ResolutionDueAtUtc);
        Assert.Equal(("PriorityPolicy", "PriorityPolicy"), (high.FirstResponseSource, high.ResolutionSource));
    }

    [Fact]
    public async Task Reopen_keeps_first_response_and_restarts_resolution_from_the_configured_row()
    {
        var setup = new Setup(enforced: true, rowFor: HoursRow);
        var ticket = Ticket.CreateUnverified("TG-1", 1, 5, (byte)PriorityLevel.Medium, "x", FridayAfternoonUtc);
        typeof(Ticket).GetProperty(nameof(Ticket.TicketId))!.SetValue(ticket, 42L);
        ticket.ClassifyRequestType(setup.RequestType.RequestTypeId);

        var initial = await setup.Service.OpenInitialPeriodAsync(ticket, FridayAfternoonUtc, null, Guid.NewGuid());
        var cycle = await setup.Service.StartReopenResolutionCycleAsync(ticket, Utc(10, 5, 10), null, Guid.NewGuid());

        Assert.NotNull(cycle);
        Assert.Equal(initial.FirstResponseDueAtUtc, cycle!.FirstResponseDueAtUtc); // carried, never recomputed
        Assert.Equal(Utc(10, 6, 14), cycle.ResolutionDueAtUtc);                    // Monday 7h + Tuesday 5h
        Assert.Contains(setup.Sla.Audit.Entries, e => e.Action == "ComputeSlaDueDates" && e.AfterValue!.Contains("\"resolutionSource\":\"RequestTypeSla:"));
    }
}
