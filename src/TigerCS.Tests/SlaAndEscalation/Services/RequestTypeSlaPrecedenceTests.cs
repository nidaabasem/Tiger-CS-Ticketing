using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// Request-type SLA precedence at runtime, on fixed instants against the
/// seeded business calendar (Asia/Dubai, 08:00–18:00, Saturday–Thursday —
/// a working day is 600 business minutes):
/// an ENFORCED request type's row for the ticket's priority replaces the
/// per-priority policy deadline by deadline, in its own units; everything
/// else keeps the per-priority policy exactly as before.
/// </summary>
public class RequestTypeSlaPrecedenceTests
{
    // Thursday 2026-10-01 16:00 Dubai (UTC+4): two business hours are left
    // on Thursday, Friday is not a working day.
    private static readonly DateTime ThursdayAfternoonUtc = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime DubaiToUtc(int year, int month, int day, int hour, int minute = 0) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-4);

    private sealed class Setup
    {
        public SlaServiceFixture Sla { get; } = new();
        public FakeRequestTypeRepository RequestTypes { get; } = new();
        public FakeRequestTypeSlaPolicyRepository RequestTypeSlas { get; } = new();
        public FakeBusinessCalendarRepository Calendar => Sla.Calendar;
        public SlaDueDateService Service { get; }
        public RequestType RequestType { get; }

        public Setup(bool enforced, RequestTypeSlaPolicy? row = null, Func<int, RequestTypeSlaPolicy>? rowFor = null)
        {
            RequestType = RequestTypes.Add(new RequestType(1, "Synthetic", 1, (byte)PriorityLevel.Medium, false, true, false, true));
            if (enforced)
            {
                RequestType.EnableConfigurationEnforcement();
            }

            var configured = rowFor?.Invoke(RequestType.RequestTypeId) ?? row;
            if (configured is not null)
            {
                RequestTypeSlas.AddAsync(configured).GetAwaiter().GetResult();
            }

            Service = new SlaDueDateService(
                Sla.Policies, Sla.Calendar, Sla.SlaInstances, Sla.Scheduler, Sla.Audit, RequestTypes, RequestTypeSlas);
        }
    }

    /// <summary>4 business hours first response, 1 business day resolution — the catalog's most common SLA.</summary>
    private static RequestTypeSlaPolicy CatalogRow(int requestTypeId, SlaClockBasis basis = SlaClockBasis.BusinessHours) =>
        new(requestTypeId, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days,
            firstResponseTargetValue: 4, firstResponseMaximumValue: null, resolutionTargetValue: 1, resolutionMaximumValue: null,
            clockBasis: basis, firstResponseUnit: SlaDurationUnit.Hours);

    [Fact]
    public async Task An_enforced_types_row_sets_both_deadlines_in_its_own_units_on_the_business_calendar()
    {
        var setup = new Setup(enforced: true, rowFor: id => CatalogRow(id));

        var due = await setup.Service.ComputeAsync((byte)PriorityLevel.Medium, setup.RequestType.RequestTypeId, ThursdayAfternoonUtc);

        // 4 business hours: Thursday 16:00–18:00 (2h), Friday off, Saturday 08:00 + 2h.
        Assert.Equal(DubaiToUtc(2026, 10, 3, 10), due.FirstResponseDueAtUtc);
        // 1 business day = one 600-minute window: Thursday 120 min + Saturday 480 min.
        Assert.Equal(DubaiToUtc(2026, 10, 3, 16), due.ResolutionDueAtUtc);
        Assert.StartsWith("RequestTypeSla:", due.FirstResponseSource, StringComparison.Ordinal);
        Assert.StartsWith("RequestTypeSla:", due.ResolutionSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Holidays_in_the_existing_calendar_are_honoured()
    {
        var setup = new Setup(enforced: true, rowFor: id => CatalogRow(id));
        setup.Calendar.Use(SlaTestCalendar.Default(new DateOnly(2026, 10, 3))); // Saturday is a holiday

        var due = await setup.Service.ComputeAsync((byte)PriorityLevel.Medium, setup.RequestType.RequestTypeId, ThursdayAfternoonUtc);

        Assert.Equal(DubaiToUtc(2026, 10, 4, 10), due.FirstResponseDueAtUtc);
        Assert.Equal(DubaiToUtc(2026, 10, 4, 16), due.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task A_24_7_row_counts_a_day_as_24_hours()
    {
        var setup = new Setup(enforced: true, rowFor: id => CatalogRow(id, SlaClockBasis.TwentyFourSeven));

        var due = await setup.Service.ComputeAsync((byte)PriorityLevel.Medium, setup.RequestType.RequestTypeId, ThursdayAfternoonUtc);

        Assert.Equal(ThursdayAfternoonUtc.AddHours(4), due.FirstResponseDueAtUtc);
        Assert.Equal(ThursdayAfternoonUtc.AddDays(1), due.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task Without_enforcement_the_same_row_is_ignored_and_the_priority_policy_applies_exactly_as_before()
    {
        var enforcedOff = new Setup(enforced: false, rowFor: id => CatalogRow(id));
        var noRequestType = new Setup(enforced: false);

        var withRow = await enforcedOff.Service.ComputeAsync((byte)PriorityLevel.Medium, enforcedOff.RequestType.RequestTypeId, ThursdayAfternoonUtc);
        var legacy = await noRequestType.Service.ComputeDueDatesAsync((byte)PriorityLevel.Medium, ThursdayAfternoonUtc);

        Assert.Equal((legacy.FirstResponseDueAtUtc, legacy.ResolutionDueAtUtc), (withRow.FirstResponseDueAtUtc, withRow.ResolutionDueAtUtc));
        Assert.Equal("PriorityPolicy", withRow.FirstResponseSource);
        // Medium's own policy: 4 business hours / 3 business days.
        Assert.Equal(DubaiToUtc(2026, 10, 3, 10), legacy.FirstResponseDueAtUtc);
        Assert.Equal(DubaiToUtc(2026, 10, 5, 16), legacy.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task A_deadline_the_row_leaves_empty_falls_back_to_the_priority_policy()
    {
        var setup = new Setup(enforced: true, rowFor: id => new RequestTypeSlaPolicy(
            id, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Hours,
            firstResponseTargetValue: null, firstResponseMaximumValue: null, resolutionTargetValue: 2, resolutionMaximumValue: null,
            clockBasis: SlaClockBasis.BusinessHours));

        var due = await setup.Service.ComputeAsync((byte)PriorityLevel.Medium, setup.RequestType.RequestTypeId, ThursdayAfternoonUtc);

        Assert.Equal("PriorityPolicy", due.FirstResponseSource);
        Assert.Equal(DubaiToUtc(2026, 10, 3, 10), due.FirstResponseDueAtUtc);
        Assert.StartsWith("RequestTypeSla:", due.ResolutionSource, StringComparison.Ordinal);
        Assert.Equal(DubaiToUtc(2026, 10, 1, 18), due.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task Another_priority_or_an_inapplicable_row_uses_the_priority_policy()
    {
        var ranged = new Setup(enforced: true, rowFor: id => new RequestTypeSlaPolicy(
            id, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, null, null, 10, 12,
            clockBasis: SlaClockBasis.BusinessHours));
        var highTicket = new Setup(enforced: true, rowFor: id => CatalogRow(id));

        var rangeDue = await ranged.Service.ComputeAsync((byte)PriorityLevel.Medium, ranged.RequestType.RequestTypeId, ThursdayAfternoonUtc);
        var highDue = await highTicket.Service.ComputeAsync((byte)PriorityLevel.High, highTicket.RequestType.RequestTypeId, ThursdayAfternoonUtc);

        Assert.Equal(("PriorityPolicy", "PriorityPolicy"), (rangeDue.FirstResponseSource, rangeDue.ResolutionSource));
        Assert.Equal(("PriorityPolicy", "PriorityPolicy"), (highDue.FirstResponseSource, highDue.ResolutionSource));
    }

    [Fact]
    public async Task Reopen_keeps_first_response_and_restarts_resolution_from_the_configured_row()
    {
        var setup = new Setup(enforced: true, rowFor: id => CatalogRow(id));
        var ticket = Ticket.CreateUnverified("TG-1", 1, 5, (byte)PriorityLevel.Medium, "x", ThursdayAfternoonUtc);
        typeof(Ticket).GetProperty(nameof(Ticket.TicketId))!.SetValue(ticket, 42L);
        ticket.ClassifyRequestType(setup.RequestType.RequestTypeId);

        var initial = await setup.Service.OpenInitialPeriodAsync(ticket, ThursdayAfternoonUtc, null, Guid.NewGuid());
        var reopenedAt = DubaiToUtc(2026, 10, 4, 9); // Sunday 09:00
        var cycle = await setup.Service.StartReopenResolutionCycleAsync(ticket, reopenedAt, null, Guid.NewGuid());

        Assert.NotNull(cycle);
        Assert.Equal(initial.FirstResponseDueAtUtc, cycle!.FirstResponseDueAtUtc); // carried, never recomputed
        Assert.Equal(DubaiToUtc(2026, 10, 5, 9), cycle.ResolutionDueAtUtc);        // one business day from the reopen
        Assert.Contains(setup.Sla.Audit.Entries, e => e.Action == "ComputeSlaDueDates" && e.AfterValue!.Contains("\"resolutionSource\":\"RequestTypeSla:"));
    }
}
