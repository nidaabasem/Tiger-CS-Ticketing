using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Domain;

public class TicketTests
{
    [Fact]
    public void CreateVerified_SetsAllFiveDimensionsAndUnitContactReferences()
    {
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260820-0001", departmentId: 2, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);

        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Equal(CrmVerificationStatus.Verified, ticket.VerificationStatus);
        Assert.Equal(EscalationLevel.None, ticket.EscalationLevel);
        Assert.Equal(SlaState.Running, ticket.SlaState);
        Assert.Null(ticket.ResolutionOutcome);
        Assert.Equal(10, ticket.UnitReferenceId);
        Assert.Equal(20, ticket.ContactReferenceId);
        Assert.Equal(2, ticket.OriginatingDepartmentId);
        Assert.Equal(2, ticket.CurrentDepartmentId);
    }

    [Fact]
    public void CreateProvisional_CriticalPriority_SucceedsWithNoUnitOrContactReference()
    {
        var ticket = Ticket.CreateProvisional(
            "TG-CS-20260820-0002", departmentId: 2, categoryId: 5,
            priorityId: (byte)PriorityLevel.Critical, "Flooding reported", DateTime.UtcNow);

        Assert.Equal(CrmVerificationStatus.PendingCrmVerification, ticket.VerificationStatus);
        Assert.Null(ticket.UnitReferenceId);
        Assert.Null(ticket.ContactReferenceId);
        // Not yet SLA-clocked (FR-TKT-09) — no due date exists to run against yet.
        Assert.Equal(SlaState.Paused, ticket.SlaState);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
    }

    [Theory]
    [InlineData(PriorityLevel.Medium)]
    [InlineData(PriorityLevel.Low)]
    public void CreateProvisional_MediumOrLowPriority_Throws_IssueSixOnlyAllowsCriticalOrHigh(PriorityLevel level)
    {
        Assert.Throws<ProvisionalTicketRequiresCriticalOrHighException>(() =>
            Ticket.CreateProvisional(
                "TG-CS-20260820-0003", departmentId: 2, categoryId: 5, (byte)level, "Leaking tap", DateTime.UtcNow));
    }

    [Fact]
    public void ReconcileVerification_OnProvisionalTicket_PopulatesReferencesAndMarksVerified()
    {
        var ticket = Ticket.CreateProvisional(
            "TG-CS-20260820-0004", departmentId: 2, categoryId: 5,
            priorityId: (byte)PriorityLevel.Critical, "Flooding reported", DateTime.UtcNow);

        ticket.ReconcileVerification(unitReferenceId: 30, contactReferenceId: 40);

        Assert.Equal(CrmVerificationStatus.Verified, ticket.VerificationStatus);
        Assert.Equal(30, ticket.UnitReferenceId);
        Assert.Equal(40, ticket.ContactReferenceId);
        Assert.Equal(SlaState.Running, ticket.SlaState);
    }

    [Fact]
    public void ReconcileVerification_OnAlreadyVerifiedTicket_Throws()
    {
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260820-0005", departmentId: 2, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);

        Assert.Throws<TicketNotPendingCrmVerificationException>(() => ticket.ReconcileVerification(30, 40));
    }
}
