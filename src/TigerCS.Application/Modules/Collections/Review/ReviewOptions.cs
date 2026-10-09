using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>Settings of the stored review data and its approval flow (<c>Collections:Review</c>).</summary>
public sealed class CollectionsReviewOptions
{
    public const string SectionName = "Collections:Review";

    /// <summary>Default for the editable "minimum remaining amount" filter (AED).</summary>
    public decimal DefaultMinimumRemaining { get; set; } = 100m;

    /// <summary>The largest list one confirmation may approve. A bigger selection must be split.</summary>
    public int MaxSelection { get; set; } = 20000;

    /// <summary>The refresh job's overall read budget (the PACT read has its own SQL timeout).</summary>
    public int RefreshTimeoutMinutes { get; set; } = 15;

    /// <summary>A run still "Running" after this long is treated as dead and may be replaced.</summary>
    public int StuckRunMinutes { get; set; } = 30;

    /// <summary>Floating-point noise tolerance in AED when normalizing a floating source value to fils.</summary>
    public decimal FloatNoiseTolerance { get; set; } = MoneyNormalizer.DefaultFloatTolerance;

    /// <summary>Review records of runs older than the newest N completed runs are deleted.</summary>
    public int RunsToKeep { get; set; } = 2;
}

/// <summary>
/// Genesys Cloud outbound upload settings (<c>Collections:GenesysOutbound</c>). The client id and secret are secrets:
/// supply them through user-secrets or environment variables, never a committed file, and they are never logged.
/// </summary>
public sealed class GenesysOutboundOptions
{
    public const string SectionName = "Collections:GenesysOutbound";
    public const int MaxBatchSize = 1000;

    /// <summary>False (default): nothing can be uploaded. Confirmation, revalidation and batching still run in tests.</summary>
    public bool Enabled { get; set; }
    public string LoginBaseUrl { get; set; } = "https://login.mypurecloud.de";
    public string ApiBaseUrl { get; set; } = "https://api.mypurecloud.de";
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public int BatchSize { get; set; } = MaxBatchSize;
    public int RequestTimeoutSeconds { get; set; } = 60;
    /// <summary>Refresh the cached token this long before its stated expiry.</summary>
    public int TokenExpirySkewSeconds { get; set; } = 60;

    /// <summary>Reminder type to Genesys contact list id. Defaults are the lists supplied for this integration.</summary>
    public Dictionary<string, string> ContactListIds { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(CampaignReminderType.CurrentMonth)] = "79e5ae74-ea6e-4941-b76d-45ddf487d8d1",
        [nameof(CampaignReminderType.FollowUp)] = "3a91c06e-47ab-4a5e-a720-004bd5cf5bba",
        [nameof(CampaignReminderType.LegalCase)] = "41178d2a-af65-45ae-9a33-b50260dc1b9b",
        [nameof(CampaignReminderType.LegalNotice)] = "372d81d7-6d2d-4b9e-8f09-cf2262aecfdf",
        [nameof(CampaignReminderType.Overdue)] = "d5f4d808-2e12-410e-8fe6-b810cca1ef4c"
    };

    /// <summary>Optional override of the ReminderType text written to Genesys; unset types use <see cref="CampaignReminderTypes.DefaultLabel"/>.</summary>
    public Dictionary<string, string> ReminderTypeLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ContactListIdFor(CampaignReminderType type) =>
        ContactListIds.TryGetValue(type.ToString(), out var id) && Guid.TryParse(id, out var parsed)
            ? parsed.ToString("D")
            : throw new InvalidOperationException($"No valid Genesys contact list id is configured for {type}.");

    public string LabelFor(CampaignReminderType type) =>
        ReminderTypeLabels.TryGetValue(type.ToString(), out var label) && !string.IsNullOrWhiteSpace(label)
            ? label.Trim() : CampaignReminderTypes.DefaultLabel(type);

    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, MaxBatchSize);
}
