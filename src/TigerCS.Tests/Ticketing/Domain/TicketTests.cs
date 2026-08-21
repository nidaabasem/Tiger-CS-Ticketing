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

    private static Ticket NewOpenTicket() =>
        Ticket.CreateVerified(
            "TG-CS-20260820-0100", departmentId: 2, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);

    [Fact]
    public void AssignTo_SetsCurrentOwner()
    {
        var ticket = NewOpenTicket();
        var employeeId = Guid.NewGuid();

        ticket.AssignTo(employeeId);

        Assert.Equal(employeeId, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public void AssignTo_EmptyGuid_Throws()
    {
        var ticket = NewOpenTicket();

        Assert.Throws<ArgumentException>(() => ticket.AssignTo(Guid.Empty));
    }

    [Fact]
    public void TransferToDepartment_MovesCurrentDepartmentAndClearsOwner_PreservesOriginating()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());

        ticket.TransferToDepartment(7);

        Assert.Equal(7, ticket.CurrentDepartmentId);
        Assert.Equal(2, ticket.OriginatingDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public void TransferToDepartment_SameDepartment_Throws()
    {
        var ticket = NewOpenTicket();

        Assert.Throws<TicketAlreadyInTargetDepartmentException>(() => ticket.TransferToDepartment(2));
    }

    [Fact]
    public void ChangeStatus_OpenToInProgress_RequiresAssignedOwner()
    {
        var ticket = NewOpenTicket();

        Assert.Throws<TicketNotAssignedException>(() => ticket.ChangeStatus(TicketStatus.InProgress));

        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
    }

    [Theory]
    [InlineData(TicketStatus.PendingCustomer)]
    [InlineData(TicketStatus.PendingThirdParty)]
    public void ChangeStatus_InProgressToPendingAndBack_Succeeds(TicketStatus pendingStatus)
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);

        ticket.ChangeStatus(pendingStatus);
        Assert.Equal(pendingStatus, ticket.TicketStatus);

        ticket.ChangeStatus(TicketStatus.InProgress);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
    }

    [Fact]
    public void ChangeStatus_DirectlyBetweenTwoPendingStates_Throws()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.ChangeStatus(TicketStatus.PendingCustomer);

        Assert.Throws<InvalidTicketStatusTransitionException>(() => ticket.ChangeStatus(TicketStatus.PendingThirdParty));
    }

    [Fact]
    public void ChangeStatus_DirectlyToResolvedOrClosed_Throws()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);

        Assert.Throws<InvalidTicketStatusTransitionException>(() => ticket.ChangeStatus(TicketStatus.Resolved));
        Assert.Throws<InvalidTicketStatusTransitionException>(() => ticket.ChangeStatus(TicketStatus.Closed));
    }

    [Fact]
    public void Resolve_FromOpen_Throws_MustBeWorkedFirst()
    {
        var ticket = NewOpenTicket();

        Assert.Throws<TicketNotEligibleForResolutionException>(
            () => ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null));
    }

    [Fact]
    public void Resolve_FromInProgress_SetsResolvedStatusAndOutcome()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);

        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);

        Assert.Equal(TicketStatus.Resolved, ticket.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Resolved, ticket.ResolutionOutcome);
        Assert.Null(ticket.DuplicateOfTicketId);
    }

    [Fact]
    public void Resolve_Duplicate_SetsDuplicateOfTicketId()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);

        ticket.Resolve(ResolutionOutcome.Duplicate, duplicateOfTicketId: 999);

        Assert.Equal((byte)ResolutionOutcome.Duplicate, ticket.ResolutionOutcome);
        Assert.Equal(999, ticket.DuplicateOfTicketId);
    }

    [Fact]
    public void Close_WithoutResolve_Throws()
    {
        var ticket = NewOpenTicket();

        Assert.Throws<TicketNotYetResolvedException>(() => ticket.Close());
    }

    [Fact]
    public void Close_AfterResolve_SetsClosed()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);

        ticket.Close();

        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
    }

    [Fact]
    public void Close_Twice_Throws_ClosedTicketImmutability()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();

        // Closing an already-Closed ticket is closed-ticket immutability
        // (PR correction), a distinct condition from "not yet resolved"
        // (Close_WithoutResolve_Throws, above).
        Assert.Throws<TicketClosedException>(() => ticket.Close());
    }

    private static Ticket NewClosedTicket()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();
        return ticket;
    }

    [Fact]
    public void AssignTo_OnClosedTicket_Throws_ClosedTicketImmutability()
    {
        var ticket = NewClosedTicket();

        Assert.Throws<TicketClosedException>(() => ticket.AssignTo(Guid.NewGuid()));
    }

    [Fact]
    public void TransferToDepartment_OnClosedTicket_Throws_ClosedTicketImmutability()
    {
        var ticket = NewClosedTicket();

        Assert.Throws<TicketClosedException>(() => ticket.TransferToDepartment(999));
    }

    [Fact]
    public void ChangeStatus_OnClosedTicket_Throws_ClosedTicketImmutability()
    {
        var ticket = NewClosedTicket();

        Assert.Throws<TicketClosedException>(() => ticket.ChangeStatus(TicketStatus.InProgress));
    }

    [Fact]
    public void Resolve_OnClosedTicket_Throws_ClosedTicketImmutability()
    {
        var ticket = NewClosedTicket();

        Assert.Throws<TicketClosedException>(() => ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null));
    }
}
