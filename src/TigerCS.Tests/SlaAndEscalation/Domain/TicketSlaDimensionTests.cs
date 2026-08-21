using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.SlaAndEscalation.Domain;

/// <summary>
/// The SLA and escalation dimensions on the ticket itself (ADR-0008's five
/// independent dimensions), including the rule this increment must not
/// weaken: closed-ticket immutability.
/// </summary>
public class TicketSlaDimensionTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);

    private static Ticket CreateVerifiedTicket(PriorityLevel priority = PriorityLevel.High) =>
        Ticket.CreateVerified(
            "TG-CS-20260823-0001", departmentId: 2, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, (byte)priority, "AC not cooling", CreatedAt);

    private static Ticket CreateInProgressTicket()
    {
        var ticket = CreateVerifiedTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        return ticket;
    }

    private static Ticket CreateClosedTicket()
    {
        var ticket = CreateInProgressTicket();
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();
        return ticket;
    }

    // -----------------------------------------------------------------
    // Escalation is a dimension, not a status (ADR-0008/ADR-0011)
    // -----------------------------------------------------------------

    /// <summary>
    /// Solution-Analysis.md §7.6: "escalating a ticket never removes it from
    /// active work". The ticket keeps its exact status, owner, resolution
    /// outcome and SLA state; only the escalation dimension moves.
    /// </summary>
    [Theory]
    [InlineData(TicketStatus.Open)]
    [InlineData(TicketStatus.InProgress)]
    public void RaisingEscalation_LeavesEveryOtherDimensionUntouched(TicketStatus status)
    {
        var ticket = status == TicketStatus.Open ? CreateVerifiedTicket() : CreateInProgressTicket();
        var ownerBefore = ticket.CurrentOwnerEmployeeId;
        var slaStateBefore = ticket.SlaState;

        ticket.RaiseEscalationLevel(EscalationLevel.Level2);

        Assert.Equal(EscalationLevel.Level2, ticket.EscalationLevel);
        Assert.Equal(status, ticket.TicketStatus);
        Assert.Equal(ownerBefore, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(slaStateBefore, ticket.SlaState);
        Assert.Null(ticket.ResolutionOutcome);
    }

    /// <summary>Escalating a Resolved ticket does not un-resolve it — the dimensions really are independent, not merely usually independent.</summary>
    [Fact]
    public void RaisingEscalation_OnAResolvedTicket_DoesNotChangeItsResolution()
    {
        var ticket = CreateInProgressTicket();
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);

        ticket.RaiseEscalationLevel(EscalationLevel.Level3);

        Assert.Equal(EscalationLevel.Level3, ticket.EscalationLevel);
        Assert.Equal(TicketStatus.Resolved, ticket.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Resolved, ticket.ResolutionOutcome);
    }

    /// <summary>ADR-0011 models the level as a state, not a counter: it only ever rises.</summary>
    [Fact]
    public void EscalationLevel_AdvancesThroughTheHierarchy()
    {
        var ticket = CreateInProgressTicket();

        ticket.RaiseEscalationLevel(EscalationLevel.Level1);
        ticket.RaiseEscalationLevel(EscalationLevel.Level2);
        ticket.RaiseEscalationLevel(EscalationLevel.Level4);

        Assert.Equal(EscalationLevel.Level4, ticket.EscalationLevel);
    }

    /// <summary>
    /// Without this, an automatic Level 2 raised by a later breach would pull
    /// a ticket back down from a Level 4 a CS Manager had already set.
    /// </summary>
    [Theory]
    [InlineData(EscalationLevel.Level2)]
    [InlineData(EscalationLevel.Level3)]
    public void EscalationLevel_IsNeverLowered(EscalationLevel requested)
    {
        var ticket = CreateInProgressTicket();
        ticket.RaiseEscalationLevel(EscalationLevel.Level4);

        var ex = Assert.Throws<EscalationLevelCannotBeLoweredException>(() => ticket.RaiseEscalationLevel(requested));

        Assert.Equal(EscalationLevel.Level4, ticket.EscalationLevel);
        Assert.Equal(EscalationLevel.Level4, ex.CurrentLevel);
    }

    /// <summary>Re-raising the level a ticket is already at is not an advance either.</summary>
    [Fact]
    public void EscalationLevel_CannotBeReRaisedToItsCurrentValue()
    {
        var ticket = CreateInProgressTicket();
        ticket.RaiseEscalationLevel(EscalationLevel.Level2);

        Assert.Throws<EscalationLevelCannotBeLoweredException>(() => ticket.RaiseEscalationLevel(EscalationLevel.Level2));
    }

    // -----------------------------------------------------------------
    // First Human Response (ISSUE-019)
    // -----------------------------------------------------------------

    [Fact]
    public void RecordFirstHumanResponse_SetsTheTimestamp()
    {
        var ticket = CreateInProgressTicket();
        var respondedAt = CreatedAt.AddMinutes(20);

        ticket.RecordFirstHumanResponse(respondedAt);

        Assert.Equal(respondedAt, ticket.FirstHumanResponseAtUtc);
    }

    /// <summary>
    /// MVP-ERD.md §2.10 — write-once at the ticket level. The First Response
    /// SLA is measured against the first engagement, so a later correction
    /// would silently move a contractual measurement.
    /// </summary>
    [Fact]
    public void RecordFirstHumanResponse_IsWriteOnce()
    {
        var ticket = CreateInProgressTicket();
        var first = CreatedAt.AddMinutes(20);
        ticket.RecordFirstHumanResponse(first);

        Assert.Throws<FirstResponseAlreadyRecordedException>(
            () => ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(5)));

        Assert.Equal(first, ticket.FirstHumanResponseAtUtc);
    }

    /// <summary>
    /// SLA-Architecture.md §16 Example E: a Genesys call answered at 08:58
    /// satisfies a ticket created at 09:00. The actual moment of human
    /// engagement is the correct value, so a pre-creation timestamp is
    /// allowed rather than rejected as nonsensical.
    /// </summary>
    [Fact]
    public void RecordFirstHumanResponse_AcceptsAMomentBeforeTicketCreation()
    {
        var ticket = CreateInProgressTicket();
        var callAnsweredAt = CreatedAt.AddMinutes(-2);

        ticket.RecordFirstHumanResponse(callAnsweredAt);

        Assert.Equal(callAnsweredAt, ticket.FirstHumanResponseAtUtc);
    }

    /// <summary>The automated acknowledgement is a separate field and never satisfies the First Response SLA (ISSUE-019).</summary>
    [Fact]
    public void AcknowledgementTimestamp_IsSeparateFromTheFirstHumanResponse()
    {
        var ticket = CreateInProgressTicket();

        Assert.Null(ticket.AcknowledgementSentAtUtc);
        Assert.Null(ticket.FirstHumanResponseAtUtc);
    }

    // -----------------------------------------------------------------
    // SlaState projection (SLA-Architecture.md §11)
    // -----------------------------------------------------------------

    [Fact]
    public void MarkSlaBreached_SetsTheTicketLevelSummary()
    {
        var ticket = CreateInProgressTicket();

        ticket.MarkSlaBreached();

        Assert.Equal(SlaState.Breached, ticket.SlaState);
    }

    /// <summary>A recorded breach is sticky: resolving inside the other clock's deadline does not turn Breached into Met.</summary>
    [Fact]
    public void MarkSlaMet_NeverOverridesARecordedBreach()
    {
        var ticket = CreateInProgressTicket();
        ticket.MarkSlaBreached();

        ticket.MarkSlaMet();

        Assert.Equal(SlaState.Breached, ticket.SlaState);
    }

    [Fact]
    public void MarkSlaMet_FromRunning_ReportsMet()
    {
        var ticket = CreateInProgressTicket();

        ticket.MarkSlaMet();

        Assert.Equal(SlaState.Met, ticket.SlaState);
    }

    /// <summary>FR-TKT-09: a ticket created provisionally during a CRM outage has not started its SLA clock.</summary>
    [Fact]
    public void ProvisionalTicket_StartsWithItsClockUnstarted()
    {
        var ticket = Ticket.CreateProvisional(
            "TG-CS-20260823-0002", departmentId: 2, categoryId: 5,
            (byte)PriorityLevel.Critical, "Flooding reported", CreatedAt);

        Assert.Equal(SlaState.Paused, ticket.SlaState);
        Assert.Equal(CrmVerificationStatus.PendingCrmVerification, ticket.VerificationStatus);
    }

    /// <summary>Reconciliation is where a provisional ticket's clock starts.</summary>
    [Fact]
    public void ReconciledTicket_StartsItsClock()
    {
        var ticket = Ticket.CreateProvisional(
            "TG-CS-20260823-0002", departmentId: 2, categoryId: 5,
            (byte)PriorityLevel.Critical, "Flooding reported", CreatedAt);

        ticket.ReconcileVerification(unitReferenceId: 10, contactReferenceId: 20);

        Assert.Equal(SlaState.Running, ticket.SlaState);
    }

    // -----------------------------------------------------------------
    // Closed-ticket immutability must remain enforced
    // -----------------------------------------------------------------

    /// <summary>A Closed ticket accepts no escalation — the increment adds two new mutating methods, and both respect the rule.</summary>
    [Fact]
    public void ClosedTicket_RejectsEscalation()
    {
        var ticket = CreateClosedTicket();

        Assert.Throws<TicketClosedException>(() => ticket.RaiseEscalationLevel(EscalationLevel.Level2));
        Assert.Equal(EscalationLevel.None, ticket.EscalationLevel);
    }

    [Fact]
    public void ClosedTicket_RejectsFirstResponseRecording()
    {
        var ticket = CreateClosedTicket();

        Assert.Throws<TicketClosedException>(() => ticket.RecordFirstHumanResponse(CreatedAt.AddHours(1)));
        Assert.Null(ticket.FirstHumanResponseAtUtc);
    }

    /// <summary>The pre-existing closed-ticket rules are unchanged by this increment.</summary>
    [Fact]
    public void ClosedTicket_StillRejectsEveryPreExistingMutation()
    {
        var ticket = CreateClosedTicket();

        Assert.Throws<TicketClosedException>(() => ticket.AssignTo(Guid.NewGuid()));
        Assert.Throws<TicketClosedException>(() => ticket.TransferToDepartment(99));
        Assert.Throws<TicketClosedException>(() => ticket.ChangeStatus(TicketStatus.InProgress));
        Assert.Throws<TicketClosedException>(() => ticket.Resolve(ResolutionOutcome.Resolved, null));
        Assert.Throws<TicketClosedException>(() => ticket.Close());
    }
}
