namespace TigerCS.Domain.Modules.CrmVerification;

/// <summary>
/// CRM cache row — never master data (ADR-0006, MVP-Data-Dictionary.md §2.7).
/// Stores only the CRM-issued identifier plus a refreshable,
/// non-authoritative display cache used for lookups and read-back during
/// verification. The CRM itself remains the sole source of truth.
/// </summary>
public class UnitReference
{
    public int UnitReferenceId { get; private set; }
    public string CrmUnitId { get; private set; } = string.Empty;
    public string UnitNumber { get; private set; } = string.Empty;
    public string? PropertyName { get; private set; }
    public string? TowerName { get; private set; }
    public string? UnitType { get; private set; }
    public DateTime LastSyncedAtUtc { get; private set; }

    private readonly List<ContactReference> _contacts = [];
    public IReadOnlyCollection<ContactReference> Contacts => _contacts;

    private UnitReference() { }

    public UnitReference(
        string crmUnitId, string unitNumber, string? propertyName, string? towerName, string? unitType, DateTime syncedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(crmUnitId))
        {
            throw new ArgumentException("CrmUnitId is required.", nameof(crmUnitId));
        }

        if (string.IsNullOrWhiteSpace(unitNumber))
        {
            throw new ArgumentException("UnitNumber is required.", nameof(unitNumber));
        }

        CrmUnitId = crmUnitId;
        UnitNumber = unitNumber;
        PropertyName = propertyName;
        TowerName = towerName;
        UnitType = unitType;
        LastSyncedAtUtc = syncedAtUtc;
    }

    /// <summary>Refreshes the display cache from a fresh CRM read — never becomes the authoritative record (ADR-0006).</summary>
    public void RefreshFromCrm(string unitNumber, string? propertyName, string? towerName, string? unitType, DateTime syncedAtUtc)
    {
        UnitNumber = unitNumber;
        PropertyName = propertyName;
        TowerName = towerName;
        UnitType = unitType;
        LastSyncedAtUtc = syncedAtUtc;
    }
}
