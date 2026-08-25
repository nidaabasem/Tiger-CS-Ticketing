namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md §2.6 — fixed, seeded reference data (SLA and
/// Escalation module ownership, MVP-ERD.md §2.6). Only the <c>Priorities</c>
/// half of §2.6 is modeled in this increment; <c>SlaPolicies</c> (due-date
/// targets, warning thresholds, escalation windows) belongs to the SLA and
/// Escalation module's own increment (MVP-Implementation-Backlog.md S-09)
/// and is out of scope here — Ticketing needs only a valid PriorityId to
/// select and route on, not the SLA math that later attaches to it.
/// </summary>
public class Priority
{
    public byte PriorityId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public byte DisplayOrder { get; private set; }

    private Priority() { }

    public Priority(byte priorityId, string name, byte displayOrder)
    {
        if (!Enum.IsDefined(typeof(PriorityLevel), priorityId))
        {
            throw new ArgumentException($"PriorityId {priorityId} is not one of the fixed MVP priorities.", nameof(priorityId));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        PriorityId = priorityId;
        Name = name;
        DisplayOrder = displayOrder;
    }
}
