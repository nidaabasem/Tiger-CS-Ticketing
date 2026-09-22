using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Ticketing.Fakes;

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
    public void CreateUnverified_SetsUnverifiedStatusAndRunningSlaWithNoUnitOrContactReference()
    {
        // Business-rule change: customer lookup no longer gates ticket
        // creation, for any priority — CreateUnverified is now the single
        // "no resolved unit/contact pair yet" path, used whether the intake
        // is not unit-related, the lookup found nothing, or a source failed.
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260820-0002", departmentId: 2, categoryId: 5,
            priorityId: (byte)PriorityLevel.Critical, "Flooding reported", DateTime.UtcNow);

        Assert.Equal(CrmVerificationStatus.Unverified, ticket.VerificationStatus);
        Assert.Null(ticket.UnitReferenceId);
        Assert.Null(ticket.ContactReferenceId);
        // Nothing pending any more — the clock starts at creation regardless of priority.
        Assert.Equal(SlaState.Running, ticket.SlaState);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
    }

    [Fact]
    public void CreateUnverified_MediumPriority_SucceedsWithNoUnitOrContactReference()
    {
        // ISSUE-006's Critical/High-only restriction is gone: any priority
        // creates a ticket the same way now — no external system's
        // availability changes that.
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260820-0006", departmentId: 2, categoryId: 5,
            priorityId: (byte)PriorityLevel.Medium, "General question about billing", DateTime.UtcNow);

        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Equal(CrmVerificationStatus.Unverified, ticket.VerificationStatus);
        Assert.Equal(EscalationLevel.None, ticket.EscalationLevel);
        Assert.Equal(SlaState.Running, ticket.SlaState);
        Assert.Null(ticket.UnitReferenceId);
        Assert.Null(ticket.ContactReferenceId);
        Assert.Equal(2, ticket.OriginatingDepartmentId);
        Assert.Equal(2, ticket.CurrentDepartmentId);
    }

    // ---- Business-rule change: the real CRM Buyer Lookup match path (GET /api/crm/buyers) ----

    [Fact]
    public void CreateVerifiedFromCrmBuyer_SetsAllFourCrmIdsAndSnapshotText_VerifiedFromCreation()
    {
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            "TG-CS-20260827-0001", departmentId: 2,
            crmBuyerCustomerId: 5001, crmBuyerLeadId: 901, crmBuyerUnitId: 101, crmBuyerProjectId: 10,
            crmBuyerCustomerName: "Ahmed Ali", crmBuyerProjectName: "Tiger Sky Tower", crmBuyerUnitNumber: "1205",
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);

        Assert.Equal(CrmVerificationStatus.Verified, ticket.VerificationStatus);
        Assert.Equal(SlaState.Running, ticket.SlaState);
        Assert.Equal(5001, ticket.CrmBuyerCustomerId);
        Assert.Equal(901, ticket.CrmBuyerLeadId);
        Assert.Equal(101, ticket.CrmBuyerUnitId);
        Assert.Equal(10, ticket.CrmBuyerProjectId);
        Assert.Equal("Ahmed Ali", ticket.CrmBuyerCustomerName);
        Assert.Equal("Tiger Sky Tower", ticket.CrmBuyerProjectName);
        Assert.Equal("1205", ticket.CrmBuyerUnitNumber);
        // A distinct identifier space from the older CRM-unit-number cache —
        // never touches UnitReferenceId/ContactReferenceId.
        Assert.Null(ticket.UnitReferenceId);
        Assert.Null(ticket.ContactReferenceId);
        Assert.Null(ticket.ManualProjectName);
        Assert.Null(ticket.ManualUnitNumber);
    }

    [Fact]
    public void CreateUnverified_WithManualProjectAndUnitNumber_StoresThemAndStaysUnverified()
    {
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260827-0002", departmentId: 2, categoryId: 5, priorityId: (byte)PriorityLevel.Medium,
            "General question", DateTime.UtcNow, manualProjectName: "Tiger Tower A", manualUnitNumber: "1204");

        Assert.Equal(CrmVerificationStatus.Unverified, ticket.VerificationStatus);
        Assert.Equal("Tiger Tower A", ticket.ManualProjectName);
        Assert.Equal("1204", ticket.ManualUnitNumber);
        Assert.Null(ticket.CrmBuyerCustomerId);
        Assert.Null(ticket.UnitReferenceId);
    }

    [Fact]
    public void CreateUnverified_WithoutManualProjectOrUnitNumber_LeavesThemNull()
    {
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260827-0003", departmentId: 2, categoryId: 5, priorityId: (byte)PriorityLevel.Medium,
            "General question", DateTime.UtcNow);

        Assert.Null(ticket.ManualProjectName);
        Assert.Null(ticket.ManualUnitNumber);
    }

    [Fact]
    public void ReconcileVerification_OnUnverifiedTicket_PopulatesReferencesAndMarksVerified()
    {
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260820-0004", departmentId: 2, categoryId: 5,
            priorityId: (byte)PriorityLevel.Critical, "Flooding reported", DateTime.UtcNow);

        ticket.ReconcileVerification(unitReferenceId: 30, contactReferenceId: 40);

        Assert.Equal(CrmVerificationStatus.Verified, ticket.VerificationStatus);
        Assert.Equal(30, ticket.UnitReferenceId);
        Assert.Equal(40, ticket.ContactReferenceId);
        // Unaffected — the clock was already running from creation.
        Assert.Equal(SlaState.Running, ticket.SlaState);
    }

    [Fact]
    public void ReconcileVerification_OnAlreadyVerifiedTicket_Throws()
    {
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260820-0005", departmentId: 2, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);

        Assert.Throws<TicketAlreadyVerifiedException>(() => ticket.ReconcileVerification(30, 40));
    }

    /// <summary>
    /// A ticket sitting in the retired PendingThirdParty status — the only way
    /// one can still exist: a row that was already in it. Written onto the
    /// entity as EF rehydrates it, never through a transition, because there
    /// is no longer one.
    /// </summary>
    private static Ticket LegacyPendingThirdPartyTicket()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        return ticket.AsLegacyPendingThirdParty();
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

    [Fact]
    public void ChangeStatus_InProgressToPendingCustomerAndBack_Succeeds()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);

        ticket.ChangeStatus(TicketStatus.PendingCustomer);
        Assert.Equal(TicketStatus.PendingCustomer, ticket.TicketStatus);

        ticket.ChangeStatus(TicketStatus.InProgress);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
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
    public void ChangeStatus_BackToOpen_Throws_AStartedTicketNeverBecomesUnstarted()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);

        Assert.Throws<InvalidTicketStatusTransitionException>(() => ticket.ChangeStatus(TicketStatus.Open));

        ticket.ChangeStatus(TicketStatus.PendingCustomer);
        Assert.Throws<InvalidTicketStatusTransitionException>(() => ticket.ChangeStatus(TicketStatus.Open));
    }

    [Fact]
    public void ChangeStatus_OpenDirectlyToPendingCustomer_Throws_WorkStartsBeforeItWaits()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());

        Assert.Throws<InvalidTicketStatusTransitionException>(() => ticket.ChangeStatus(TicketStatus.PendingCustomer));
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
    }

    // ---- PendingThirdParty: legacy readable, never a target ----

    [Theory]
    [InlineData(TicketStatus.Open)]
    [InlineData(TicketStatus.InProgress)]
    [InlineData(TicketStatus.PendingCustomer)]
    public void ChangeStatus_ToPendingThirdParty_Throws_FromEveryActiveStatus(TicketStatus from)
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        if (from is not TicketStatus.Open)
        {
            ticket.ChangeStatus(TicketStatus.InProgress);
        }

        if (from is TicketStatus.PendingCustomer)
        {
            ticket.ChangeStatus(TicketStatus.PendingCustomer);
        }

        Assert.Throws<InvalidTicketStatusTransitionException>(() => ticket.ChangeStatus(TicketStatus.PendingThirdParty));
        Assert.Equal(from, ticket.TicketStatus);
    }

    [Fact]
    public void ChangeStatus_ToPendingThirdParty_Throws_EvenFromALegacyPendingThirdPartyTicket()
    {
        // The retired status is not even self-reachable: a legacy ticket's one
        // move is forward, out of it.
        var ticket = LegacyPendingThirdPartyTicket();

        Assert.Throws<InvalidTicketStatusTransitionException>(() => ticket.ChangeStatus(TicketStatus.PendingThirdParty));
        Assert.Equal(TicketStatus.PendingThirdParty, ticket.TicketStatus);
    }

    [Fact]
    public void ChangeStatus_LegacyPendingThirdPartyToInProgress_Succeeds_TheEscapePath()
    {
        var ticket = LegacyPendingThirdPartyTicket();

        ticket.ChangeStatus(TicketStatus.InProgress);

        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
    }

    [Fact]
    public void TransitionTable_OffersPendingThirdPartyAsATargetFromNoStatusAtAll()
    {
        // The guarantee stated structurally rather than case by case: no source
        // status, active or legacy, lists the retired one among its targets.
        foreach (var from in Enum.GetValues<TicketStatus>())
        {
            Assert.DoesNotContain(TicketStatus.PendingThirdParty, TicketStatusTransitions.AllowedTargetsFrom(from));
            Assert.False(TicketStatusTransitions.IsChangeStatusAllowed(from, TicketStatus.PendingThirdParty));
        }
    }

    [Fact]
    public void TransitionTable_OffersExactlyTheApprovedTargets()
    {
        Assert.Equal([TicketStatus.InProgress], TicketStatusTransitions.AllowedTargetsFrom(TicketStatus.Open));
        Assert.Equal([TicketStatus.PendingCustomer], TicketStatusTransitions.AllowedTargetsFrom(TicketStatus.InProgress));
        Assert.Equal([TicketStatus.InProgress], TicketStatusTransitions.AllowedTargetsFrom(TicketStatus.PendingCustomer));
        Assert.Equal([TicketStatus.InProgress], TicketStatusTransitions.AllowedTargetsFrom(TicketStatus.PendingThirdParty));

        // Resolved and Closed leave the working sub-machine through Resolve,
        // Close and Reopen — dedicated operations, never a generic status change.
        Assert.Empty(TicketStatusTransitions.AllowedTargetsFrom(TicketStatus.Resolved));
        Assert.Empty(TicketStatusTransitions.AllowedTargetsFrom(TicketStatus.Closed));
    }

    [Fact]
    public void ActiveStatuses_ExcludeTheLegacyOneAndNothingElse()
    {
        Assert.Equal(
            [TicketStatus.Open, TicketStatus.InProgress, TicketStatus.PendingCustomer, TicketStatus.Resolved, TicketStatus.Closed],
            TicketStatusTransitions.ActiveStatuses);

        Assert.True(TicketStatusTransitions.IsLegacyOnly(TicketStatus.PendingThirdParty));
        Assert.All(
            TicketStatusTransitions.ActiveStatuses,
            s => Assert.False(TicketStatusTransitions.IsLegacyOnly(s)));
    }

    [Fact]
    public void PendingThirdPartyKeepsItsStoredEnumValue_HistoricalRowsAreNeverReinterpreted()
    {
        // Renumbering would silently turn every stored 4 into another status.
        Assert.Equal(4, (byte)TicketStatus.PendingThirdParty);
        Assert.Equal(1, (byte)TicketStatus.Open);
        Assert.Equal(2, (byte)TicketStatus.InProgress);
        Assert.Equal(3, (byte)TicketStatus.PendingCustomer);
        Assert.Equal(5, (byte)TicketStatus.Resolved);
        Assert.Equal(6, (byte)TicketStatus.Closed);
    }

    [Fact]
    public void Resolve_FromOpen_Throws_MustBeWorkedFirst()
    {
        var ticket = NewOpenTicket();

        Assert.Throws<TicketNotEligibleForResolutionException>(
            () => ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null));
    }

    [Fact]
    public void Resolve_FromInProgress_SetsResolvedStatusAndOutcome_PendingCustomerIsNotMandatory()
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
    public void Resolve_FromPendingCustomer_Succeeds()
    {
        var ticket = NewOpenTicket();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.ChangeStatus(TicketStatus.PendingCustomer);

        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);

        Assert.Equal(TicketStatus.Resolved, ticket.TicketStatus);
    }

    [Fact]
    public void Resolve_FromLegacyPendingThirdParty_Throws_ItReturnsToInProgressFirst()
    {
        var ticket = LegacyPendingThirdPartyTicket();

        Assert.Throws<TicketNotEligibleForResolutionException>(
            () => ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null));
        Assert.Equal(TicketStatus.PendingThirdParty, ticket.TicketStatus);

        // The escape path is the whole answer: back to InProgress, then resolve
        // exactly like any other ticket.
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        Assert.Equal(TicketStatus.Resolved, ticket.TicketStatus);
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

    // ---- Unclassified tickets ----
    //
    // A ticket created from an inquiry an agent has picked up but nobody has
    // read yet: the department is known, the request is not. CategoryId is
    // null because that is the truth, and a placeholder category would drive
    // reporting, queues and the agent's screen with a classification nobody
    // made.

    [Fact]
    public void CreateUnclassified_HasNoCategoryNoPriority_AndDeliberatelyNoSlaClock()
    {
        var ticket = Ticket.CreateUnclassified(
            "TG-CS-20260910-0001", departmentId: 2,
            "Customer asked about an NOC", DateTime.UtcNow);

        Assert.Null(ticket.CategoryId);
        Assert.False(ticket.IsClassified);
        Assert.Null(ticket.RequestTypeId);
        // Nobody has judged this ticket's urgency, so it carries no tier —
        // not a defaulted Medium that would rank it against triaged tickets.
        Assert.Null(ticket.PriorityId);
        // The SLA policy is chosen by priority, and this ticket has none — so
        // no period is opened rather than the wrong one.
        Assert.Equal(SlaState.NotApplicable, ticket.SlaState);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Equal(CrmVerificationStatus.Unverified, ticket.VerificationStatus);
        Assert.Equal(2, ticket.OriginatingDepartmentId);
        Assert.Equal(2, ticket.CurrentDepartmentId);
    }

    [Fact]
    public void Classify_SetsTheCategoryAndTheFirstRealPriority_OnTheSameTicket()
    {
        var ticket = Ticket.CreateUnclassified(
            "TG-CS-20260910-0001", 2, "Customer asked about an NOC", DateTime.UtcNow);

        Assert.Null(ticket.PriorityId);

        ticket.Classify(categoryId: 5, priorityId: (byte)PriorityLevel.High);

        Assert.True(ticket.IsClassified);
        Assert.Equal(5, ticket.CategoryId);
        Assert.Equal((byte)PriorityLevel.High, ticket.PriorityId);
        Assert.Equal("TG-CS-20260910-0001", ticket.TicketNumber);
    }

    [Fact]
    public void Classify_IsWriteOnce_SoAClassifiedTicketIsNeverSilentlyReCategorised()
    {
        var ticket = Ticket.CreateUnclassified(
            "TG-CS-20260910-0001", 2, "Customer asked about an NOC", DateTime.UtcNow);
        ticket.Classify(categoryId: 5, priorityId: (byte)PriorityLevel.High);

        var refused = Assert.Throws<TicketAlreadyClassifiedException>(
            () => ticket.Classify(categoryId: 6, priorityId: (byte)PriorityLevel.Low));

        Assert.Equal(5, refused.CategoryId);
        Assert.Equal(5, ticket.CategoryId);
        Assert.Equal((byte)PriorityLevel.High, ticket.PriorityId);
    }

    [Fact]
    public void Classify_AlsoRefusesATicketThatWasCreatedWithACategory()
    {
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260910-0002", departmentId: 2, categoryId: 7,
            priorityId: (byte)PriorityLevel.Medium, "AC not cooling", DateTime.UtcNow);

        Assert.Throws<TicketAlreadyClassifiedException>(
            () => ticket.Classify(categoryId: 8, priorityId: (byte)PriorityLevel.High));
    }

    [Fact]
    public void StartSlaClock_MovesOnlyANotApplicableTicket_AndNeverRestartsARunningOne()
    {
        var unclassified = Ticket.CreateUnclassified(
            "TG-CS-20260910-0001", 2, "Customer asked about an NOC", DateTime.UtcNow);
        unclassified.StartSlaClock();
        Assert.Equal(SlaState.Running, unclassified.SlaState);

        // A ticket already being measured is untouched: the SLA dimension is
        // owned by the SLA services, and this transition only closes the gap
        // an unclassified ticket left open.
        var paused = Ticket.CreateUnverified(
            "TG-CS-20260910-0002", 2, categoryId: 7, priorityId: (byte)PriorityLevel.Medium, "AC not cooling", DateTime.UtcNow);
        paused.MarkSlaBreached();
        paused.StartSlaClock();
        Assert.Equal(SlaState.Breached, paused.SlaState);
    }
}
