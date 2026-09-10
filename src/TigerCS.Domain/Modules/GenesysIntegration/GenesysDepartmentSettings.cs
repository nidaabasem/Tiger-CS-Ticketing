namespace TigerCS.Domain.Modules.GenesysIntegration;

/// <summary>
/// Genesys integration phase 1 — per-department settings for tickets
/// created from a Genesys inquiry. Every TigerCS ticket requires an active
/// Ticket Category (the existing FR-CLS-01 rule; <c>Tickets.CategoryId</c>
/// is NOT NULL and this phase does not change that), but a Genesys inquiry
/// arrives before anyone has classified it — Request Type / Category /
/// Priority are the agent's or the workflow's later decision. So each
/// department that receives Genesys inquiries names the category its
/// unclassified Genesys tickets are created under (typically a
/// "General Inquiry"-style category of that department), as configuration
/// rather than a name comparison or a "first active category" guess. No
/// Request Type is ever inferred from this — a Genesys ticket starts with
/// <c>RequestTypeId = null</c> exactly like any other unclassified ticket.
///
/// <para>
/// One row per department (unique). A department with no row (or an
/// inactive one) cannot receive Genesys tickets: ingestion reports the gap
/// as a configuration outcome rather than picking a category on its own.
/// </para>
/// </summary>
public class GenesysDepartmentSettings
{
    public int GenesysDepartmentSettingsId { get; private set; }
    public int DepartmentId { get; private set; }

    /// <summary>The category Genesys-created tickets of this department start under. Must belong to <see cref="DepartmentId"/> and be active — validated by the administration service, not here.</summary>
    public int DefaultCategoryId { get; private set; }

    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private GenesysDepartmentSettings() { }

    public GenesysDepartmentSettings(int departmentId, int defaultCategoryId, DateTime createdAtUtc, bool isActive = true)
    {
        DepartmentId = ValidatePositive(departmentId, nameof(departmentId));
        DefaultCategoryId = ValidatePositive(defaultCategoryId, nameof(defaultCategoryId));
        IsActive = isActive;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public void Update(int defaultCategoryId, bool isActive, DateTime updatedAtUtc)
    {
        DefaultCategoryId = ValidatePositive(defaultCategoryId, nameof(defaultCategoryId));
        IsActive = isActive;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static int ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Must reference a real configuration row.");
        }

        return value;
    }
}
