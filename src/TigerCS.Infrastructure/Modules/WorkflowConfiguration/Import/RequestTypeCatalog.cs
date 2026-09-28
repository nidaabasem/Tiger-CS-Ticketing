using System.Text.Json;
using System.Text.Json.Serialization;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Import;

/// <summary>
/// One row of the business's request-type catalog workbook
/// (docs/imports/TigerCS_New_Request_Types.xlsx, Sheet2), exactly as
/// written there — typos and free text included. Interpretation happens only
/// in <see cref="RequestTypeCatalogMapper"/>, never here.
/// </summary>
public sealed record RequestTypeCatalogRow(
    string RequestCode,
    string Department,
    string? RequestGroup,
    string Name,
    string? BusinessDescription,
    string ProposedWorkflow,
    string NeedsApproval,
    string? ApprovalRole,
    string DefaultPriority,
    string FirstResponseSla,
    string ResolutionSla,
    string? RequiredFields,
    string? RequiredDocuments,
    string AllowTransfer,
    string AllowReopen,
    string? BusinessDecision,
    string? BusinessComments);

/// <summary>
/// Loads the embedded catalog — the verbatim JSON extract of the workbook's
/// Sheet2, embedded in this assembly so an import can only ever run against
/// the file that was reviewed.
/// </summary>
public static class RequestTypeCatalog
{
    public const string ResourceName = "TigerCS.RequestTypeCatalog.TigerCS_New_Request_Types.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static IReadOnlyList<RequestTypeCatalogRow> Load()
    {
        using var stream = typeof(RequestTypeCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded request-type catalog '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<RequestTypeCatalogRow> Parse(string json)
    {
        var document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("The request-type catalog is empty.");

        var rows = document.Rows ?? [];
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.RequestCode))
            {
                throw new InvalidOperationException("Every catalog row needs a Request Code — it is the import's stable identifier.");
            }
        }

        var duplicates = rows
            .GroupBy(r => r.RequestCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "The request-type catalog repeats Request Code(s) " + string.Join(", ", duplicates) + "; each code must be unique.");
        }

        return rows;
    }

    private sealed class CatalogDocument
    {
        [JsonPropertyName("rows")]
        public List<RequestTypeCatalogRow>? Rows { get; init; }
    }
}
