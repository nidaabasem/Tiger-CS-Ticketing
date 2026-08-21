namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// MVP-ERD.md §2.12 / MVP-Data-Dictionary.md §2.12 — append-only assignment
/// history. A reassignment inserts a new row and flips the prior current
/// row's <see cref="IsCurrent"/> to false; it never updates the prior row's
/// <see cref="AssignedEmployeeId"/>.
/// </summary>
public class TicketAssignment
{
    public long TicketAssignmentId { get; private set; }
    public long TicketId { get; private set; }
    public Guid AssignedEmployeeId { get; private set; }
    public int AssignedDepartmentId { get; private set; }
    public DateTime AssignedAtUtc { get; private set; }
    public Guid? AssigningActorEmployeeId { get; private set; }
    public bool IsCurrent { get; private set; }

    private TicketAssignment() { }

    public TicketAssignment(
        long ticketId,
        Guid assignedEmployeeId,
        int assignedDepartmentId,
        DateTime assignedAtUtc,
        Guid? assigningActorEmployeeId)
    {
        if (assignedEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("AssignedEmployeeId is required.", nameof(assignedEmployeeId));
        }

        TicketId = ticketId;
        AssignedEmployeeId = assignedEmployeeId;
        AssignedDepartmentId = assignedDepartmentId;
        AssignedAtUtc = assignedAtUtc;
        AssigningActorEmployeeId = assigningActorEmployeeId;
        IsCurrent = true;
    }

    /// <summary>Flipped when a later assignment supersedes this one — append-only, this row is never deleted (MVP-ERD.md §2.12).</summary>
    public void MarkSuperseded() => IsCurrent = false;
}
