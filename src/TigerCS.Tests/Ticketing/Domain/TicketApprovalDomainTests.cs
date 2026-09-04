using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Tests.Ticketing.Domain;

/// <summary>
/// Approval-cycle invariants (Workflow/Automation phase 3): Pending-only
/// decisions, write-once outcomes, mandatory rejection reasons, target
/// snapshots, and append-plus-supersede history.
/// </summary>
public class TicketApprovalDomainTests
{
    private static readonly DateTime Now = new(2026, 9, 4, 10, 15, 0, DateTimeKind.Utc);

    private static TicketApproval PendingApproval(ApprovalType type = ApprovalType.AccountingApproval)
    {
        var requirement = RequestTypeApprovalRequirement.ForDepartment(requestTypeId: 1, type, targetDepartmentId: 9);
        return TicketApproval.Request(ticketId: 42, requirement, Guid.NewGuid(), Now, "Please approve", Guid.NewGuid());
    }

    [Fact]
    public void Request_SnapshotsTheRequirementTarget_AndStartsPendingAndCurrent()
    {
        var approval = PendingApproval();

        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.True(approval.IsCurrent);
        Assert.Equal(ApprovalTargetKind.Department, approval.TargetKind);
        Assert.Equal(9, approval.TargetDepartmentId);
        Assert.Equal(Now, approval.RequestedAtUtc);
        Assert.Null(approval.DecisionAtUtc);
    }

    [Fact]
    public void Decisions_AreWriteOnce()
    {
        var approval = PendingApproval();
        approval.Approve(Guid.NewGuid(), Now.AddHours(1), "OK");

        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        Assert.Equal(Now.AddHours(1), approval.DecisionAtUtc);

        Assert.Throws<ApprovalAlreadyDecidedException>(() => approval.Approve(Guid.NewGuid(), Now.AddHours(2), null));
        Assert.Throws<ApprovalAlreadyDecidedException>(() => approval.Reject(Guid.NewGuid(), Now.AddHours(2), "no"));
        Assert.Throws<ApprovalAlreadyDecidedException>(() => approval.Cancel(Guid.NewGuid(), Now.AddHours(2), null));
    }

    [Fact]
    public void Reject_RequiresAReason()
    {
        var approval = PendingApproval();

        Assert.Throws<ArgumentException>(() => approval.Reject(Guid.NewGuid(), Now.AddHours(1), "  "));
        Assert.Equal(ApprovalStatus.Pending, approval.Status);

        approval.Reject(Guid.NewGuid(), Now.AddHours(1), "Payment record not found");
        Assert.Equal(ApprovalStatus.Rejected, approval.Status);
        Assert.Equal("Payment record not found", approval.DecisionComment);
    }

    [Fact]
    public void SupersededHistory_IsPreservedNotOverwritten()
    {
        var rejected = PendingApproval();
        rejected.Reject(Guid.NewGuid(), Now.AddHours(1), "Missing invoice");
        rejected.MarkSuperseded();

        // The rejected cycle keeps its full story; the new cycle is a
        // separate record.
        Assert.Equal(ApprovalStatus.Rejected, rejected.Status);
        Assert.False(rejected.IsCurrent);
        Assert.Equal("Missing invoice", rejected.DecisionComment);

        var second = PendingApproval();
        Assert.True(second.IsCurrent);
        Assert.Equal(ApprovalStatus.Pending, second.Status);
    }

    [Fact]
    public void RequirementFactories_ValidateTheirTargets()
    {
        Assert.Throws<ArgumentException>(() => RequestTypeApprovalRequirement.ForRole(1, ApprovalType.CustomerServiceApproval, " "));
        Assert.Throws<ArgumentException>(() => RequestTypeApprovalRequirement.ForDepartment(1, ApprovalType.AccountingApproval, 9, "Not A Real Role"));
        Assert.Throws<ArgumentException>(() => RequestTypeApprovalRequirement.ForEmployee(1, ApprovalType.AccountingApproval, Guid.Empty));

        var roleTarget = RequestTypeApprovalRequirement.ForRole(1, ApprovalType.CustomerServiceApproval, "CS Supervisor");
        Assert.Equal(ApprovalTargetKind.Role, roleTarget.TargetKind);
        Assert.Equal("CS Supervisor", roleTarget.TargetRoleName);
        Assert.True(roleTarget.BlocksWorkUntilApproved);
    }

    [Fact]
    public void WorkflowEvent_RejectsUndefinedEventTypes()
    {
        Assert.Throws<ArgumentException>(() => new TicketWorkflowEvent(
            1, (WorkflowEventType)99, Now, Guid.NewGuid(), null, null, Guid.NewGuid()));
    }
}
