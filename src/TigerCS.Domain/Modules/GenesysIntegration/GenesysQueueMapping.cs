namespace TigerCS.Domain.Modules.GenesysIntegration;

/// <summary>
/// Genesys integration phase 1 — the configured <b>Genesys Queue → TigerCS
/// Department</b> mapping. Genesys owns routing (which queue an inquiry
/// lands in); TigerCS only needs to know which department a queue's
/// inquiries belong to when the customer did not choose one explicitly
/// (WhatsApp, social media, phone). Configuration rows, never code: no
/// queue id or department name is hard-coded anywhere in the integration,
/// and the real Genesys queue ids are entered by an administrator once the
/// Genesys team supplies them — none are seeded, because none are known.
///
/// <para>
/// <see cref="QueueId"/> is Genesys' own identifier, stored as an opaque
/// string (case-insensitive unique). Rows are deactivated, never deleted,
/// so an inquiry that arrived under a retired mapping can still be
/// explained by history.
/// </para>
/// </summary>
public class GenesysQueueMapping
{
    public const int QueueIdMaxLength = 64;
    public const int QueueNameMaxLength = 200;

    public int GenesysQueueMappingId { get; private set; }

    /// <summary>Genesys' queue identifier — the routing key an inbound inquiry carries.</summary>
    public string QueueId { get; private set; } = string.Empty;

    /// <summary>Display name for administrators (Genesys' queue name, where known). Never used for matching.</summary>
    public string? QueueName { get; private set; }

    public int DepartmentId { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private GenesysQueueMapping() { }

    public GenesysQueueMapping(string queueId, string? queueName, int departmentId, DateTime createdAtUtc, bool isActive = true)
    {
        QueueId = ValidateQueueId(queueId);
        QueueName = TrimName(queueName);
        DepartmentId = ValidateDepartmentId(departmentId);
        IsActive = isActive;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    /// <summary>Administration edit — re-points the queue at another department and/or renames it; the queue id itself is the identity and never changes.</summary>
    public void Update(string? queueName, int departmentId, bool isActive, DateTime updatedAtUtc)
    {
        QueueName = TrimName(queueName);
        DepartmentId = ValidateDepartmentId(departmentId);
        IsActive = isActive;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static string ValidateQueueId(string queueId)
    {
        if (string.IsNullOrWhiteSpace(queueId))
        {
            throw new ArgumentException("QueueId is required.", nameof(queueId));
        }

        var trimmed = queueId.Trim();
        if (trimmed.Length > QueueIdMaxLength)
        {
            throw new ArgumentException($"QueueId must be at most {QueueIdMaxLength} characters.", nameof(queueId));
        }

        return trimmed;
    }

    private static int ValidateDepartmentId(int departmentId)
    {
        if (departmentId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(departmentId), "DepartmentId must reference a real department.");
        }

        return departmentId;
    }

    private static string? TrimName(string? queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName))
        {
            return null;
        }

        var trimmed = queueName.Trim();
        return trimmed.Length <= QueueNameMaxLength ? trimmed : trimmed[..QueueNameMaxLength];
    }
}
