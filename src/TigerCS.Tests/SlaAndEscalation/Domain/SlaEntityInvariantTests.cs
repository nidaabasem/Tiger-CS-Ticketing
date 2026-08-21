using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.SlaAndEscalation.Domain;

/// <summary>
/// The invariants MVP-ERD.md §2.15/§2.17 and ADR-0011 place on the SLA-period
/// and escalation rows themselves, independent of any service.
/// </summary>
public class SlaEntityInvariantTests
{
    private static readonly DateTime Start = new(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);

    private static TicketSlaInstance OpenPeriod(byte priorityId = (byte)PriorityLevel.High) =>
        TicketSlaInstance.OpenInitialPeriod(
            ticketId: 1, priorityId, Start, Start.AddHours(1), Start.AddHours(24));

    // -----------------------------------------------------------------
    // MVP-ERD.md §2.15 — "the single highest-consequence integrity rule
    // in this schema": a breach flag, once true, is never reset.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(SlaDeadlineType.FirstResponse)]
    [InlineData(SlaDeadlineType.Resolution)]
    public void BreachFlag_OnceSet_StaysSet(SlaDeadlineType deadlineType)
    {
        var instance = OpenPeriod();

        Assert.True(instance.MarkBreached(deadlineType));
        Assert.True(instance.IsBreached(deadlineType));

        // Re-marking reports "nothing changed" and, crucially, does not
        // clear it. There is no code path at all that sets a flag false —
        // MarkBreached is the only writer and takes no bool.
        Assert.False(instance.MarkBreached(deadlineType));
        Assert.True(instance.IsBreached(deadlineType));
    }

    /// <summary>ADR-0009's "fully independent pairs": breaching one clock says nothing about the other.</summary>
    [Fact]
    public void TheTwoClocksBreachIndependently()
    {
        var instance = OpenPeriod();

        instance.MarkBreached(SlaDeadlineType.FirstResponse);

        Assert.True(instance.FirstResponseBreached);
        Assert.False(instance.ResolutionBreached);
    }

    [Fact]
    public void OpenInitialPeriod_RecordsTheClockStartAndBothDueDates()
    {
        var instance = OpenPeriod();

        Assert.Equal(Start, instance.PeriodStartAtUtc);
        Assert.Equal(Start.AddHours(1), instance.DueAtUtcFor(SlaDeadlineType.FirstResponse));
        Assert.Equal(Start.AddHours(24), instance.DueAtUtcFor(SlaDeadlineType.Resolution));
        Assert.Equal(SlaChangeReason.InitialCreation, instance.ChangeReason);

        // Null PeriodEndAtUtc is what marks this the ticket's current period
        // (MVP-ERD.md §2.15); nothing in this increment closes one.
        Assert.Null(instance.PeriodEndAtUtc);
        Assert.Null(instance.ApprovedByEmployeeId);
    }

    /// <summary>A due date before the clock even started would be a permanently-breached period; it is refused at construction.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DueDateBeforeTheClockStart_IsRejected(bool firstResponseIsEarly)
    {
        Assert.Throws<ArgumentException>(() => TicketSlaInstance.OpenInitialPeriod(
            ticketId: 1,
            (byte)PriorityLevel.High,
            Start,
            firstResponseIsEarly ? Start.AddMinutes(-1) : Start.AddHours(1),
            firstResponseIsEarly ? Start.AddHours(24) : Start.AddMinutes(-1)));
    }

    // -----------------------------------------------------------------
    // MVP-Data-Dictionary.md §2.17 / ADR-0011 — NotifiedRoles is distinct
    // from Level, and follows the approved recipient matrix.
    // -----------------------------------------------------------------

    /// <summary>
    /// SLA-Architecture.md §11's breach-recipient matrix: Critical and High
    /// notify the Department Head and the GM together (ISSUE-004), Medium the
    /// Department Head, Low the Supervisor. The escalation is Level 2 in
    /// every case — notifying the GM does not, by itself, make a ticket
    /// Level 3.
    /// </summary>
    [Theory]
    [InlineData(PriorityLevel.Critical, Roles.DepartmentHead + "," + Roles.GeneralManager)]
    [InlineData(PriorityLevel.High, Roles.DepartmentHead + "," + Roles.GeneralManager)]
    [InlineData(PriorityLevel.Medium, Roles.DepartmentHead)]
    [InlineData(PriorityLevel.Low, Roles.CsSupervisor)]
    public void AutomaticBreachEscalation_NotifiesTheApprovedRecipients(PriorityLevel priority, string expectedRoles)
    {
        var escalation = TicketEscalation.RaiseAutomaticLevel2OnBreach(ticketId: 1, (byte)priority, Start);

        Assert.Equal(expectedRoles, escalation.NotifiedRoles);
        Assert.Equal((byte)EscalationLevel.Level2, escalation.Level);
        Assert.Equal(EscalationTriggerType.AutoBreach, escalation.TriggerType);
    }

    /// <summary>Solution-Analysis.md §7.6's hierarchy: Level 2 the Department Head, Level 3 the GM, Level 4 the Chairman/CEO.</summary>
    [Theory]
    [InlineData(EscalationLevel.Level2, EscalationTriggerType.ManualFlag, Roles.DepartmentHead)]
    [InlineData(EscalationLevel.Level3, EscalationTriggerType.ManualFlag, Roles.GeneralManager)]
    [InlineData(EscalationLevel.Level4, EscalationTriggerType.ManualLevel4, Roles.ChairmanCeo)]
    public void ManualEscalation_NotifiesTheLevelsOwnRole(
        EscalationLevel level, EscalationTriggerType triggerType, string expectedRole)
    {
        var escalation = TicketEscalation.RaiseManual(ticketId: 1, (byte)level, triggerType, Start);

        Assert.Equal(expectedRole, escalation.NotifiedRoles);
        Assert.Equal((byte)level, escalation.Level);
    }

    /// <summary>
    /// MVP-API-Contracts.md §5.7 exposes only the manual trigger types; the
    /// automatic ones are system-generated. A caller cannot fabricate an
    /// AutoBreach row through the manual factory and so cannot forge the
    /// appearance of an SLA breach.
    /// </summary>
    [Theory]
    [InlineData(EscalationTriggerType.AutoBreach)]
    [InlineData(EscalationTriggerType.AutoWindowExpired)]
    public void ManualFactory_RejectsTheAutomaticTriggerTypes(EscalationTriggerType triggerType)
    {
        Assert.Throws<ArgumentException>(() =>
            TicketEscalation.RaiseManual(ticketId: 1, (byte)EscalationLevel.Level2, triggerType, Start));
    }

    /// <summary>A newly raised escalation has no response recorded — §5.8's respond action does not ship in this increment.</summary>
    [Fact]
    public void NewEscalation_HasNoResponseRecorded()
    {
        var escalation = TicketEscalation.RaiseManual(ticketId: 1, (byte)EscalationLevel.Level2, EscalationTriggerType.ManualFlag, Start);

        Assert.Null(escalation.RespondedAtUtc);
        Assert.Null(escalation.RespondingEmployeeId);
        Assert.Equal(Start, escalation.RaisedAtUtc);
    }

    // -----------------------------------------------------------------
    // SlaPolicy guards
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(0, 240)]
    [InlineData(15, 0)]
    [InlineData(-15, 240)]
    public void SlaPolicy_RejectsNonPositiveTargets(int firstResponseMinutes, int resolutionMinutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlaPolicy(
            (byte)PriorityLevel.Critical, firstResponseMinutes, resolutionMinutes,
            SlaClockBasis.TwentyFourSeven, 50.00m, 30, 1));
    }

    [Fact]
    public void SlaPolicy_RejectsAPriorityOutsideTheFixedSet()
    {
        Assert.Throws<ArgumentException>(() => new SlaPolicy(
            priorityId: 9, 15, 240, SlaClockBasis.TwentyFourSeven, 50.00m, 30, 1));
    }

    [Fact]
    public void SlaPolicy_SelectsTheRightTargetPerDeadlineType()
    {
        var policy = new SlaPolicy((byte)PriorityLevel.High, 60, 1440, SlaClockBasis.BusinessHours, 75.00m, 2, 2);

        Assert.Equal(60, policy.TargetMinutesFor(SlaDeadlineType.FirstResponse));
        Assert.Equal(1440, policy.TargetMinutesFor(SlaDeadlineType.Resolution));
    }

    // -----------------------------------------------------------------
    // BusinessCalendar / Holiday guards
    // -----------------------------------------------------------------

    [Fact]
    public void BusinessCalendar_RejectsAnInvertedWindow()
    {
        Assert.Throws<ArgumentException>(() => new BusinessCalendar(
            "Broken", new TimeOnly(18, 0), new TimeOnly(8, 0), "Asia/Dubai", DateTime.UnixEpoch));
    }

    /// <summary>ISSUE-012's split ownership: a holiday always records the technical administrator who entered it, even before a business owner confirms it.</summary>
    [Fact]
    public void Holiday_RequiresTheEnteringEmployee()
    {
        Assert.Throws<ArgumentException>(() =>
            new Holiday(1, new DateOnly(2026, 12, 2), "National Day", Guid.Empty));
    }

    [Fact]
    public void Holiday_IsUnconfirmedUntilABusinessOwnerConfirmsIt()
    {
        var holiday = new Holiday(1, new DateOnly(2026, 12, 2), "National Day", Guid.NewGuid());

        Assert.Null(holiday.ConfirmedByEmployeeId);
        Assert.Null(holiday.ConfirmedAtUtc);
    }

    /// <summary>MVP-ERD.md §2.20's 1:7 cardinality — a calendar covers every day of the week, working or not.</summary>
    [Fact]
    public void BusinessCalendarWorkingDay_RoundTripsItsDayOfWeek()
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var row = new BusinessCalendarWorkingDay(1, day, isWorkingDay: day != DayOfWeek.Friday);

            Assert.Equal(day, row.AsDayOfWeek());
            Assert.Equal(day != DayOfWeek.Friday, row.IsWorkingDay);
        }
    }
}
