namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>MVP-Data-Dictionary.md §2.10/§2.14, Solution-Analysis.md §5.5. Set once, at the Resolve action; one of the five independent lifecycle dimensions (ADR-0008).</summary>
public enum ResolutionOutcome : byte
{
    Resolved = 1,
    Cancelled = 2,
    Rejected = 3,
    Duplicate = 4
}
