namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// The fixed MVP priority set (MVP-Data-Dictionary.md §2.6 — "PK — fixed
/// set: 1=Critical, 2=High, 3=Medium, 4=Low"). Numeric values match
/// <see cref="Priority.PriorityId"/> exactly so a raw <c>PriorityId</c> can
/// be cast to/compared against this enum without a lookup.
/// </summary>
public enum PriorityLevel : byte
{
    Critical = 1,
    High = 2,
    Medium = 3,
    Low = 4
}
