using System.Globalization;
using System.Text;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;

namespace TigerCS.Tests.WorkflowConfiguration.Services;

/// <summary>
/// Renders the generated data block of ImportNewRequestTypes_UAT.sql from
/// <see cref="NewRequestTypesBusinessReview"/>, so the script and the C#
/// importer can never carry different rows: the drift test compares the
/// committed block against this rendering byte for byte.
/// Regenerate with <c>TIGERCS_REGENERATE_UAT_SQL=1 dotnet test --filter NewRequestTypesImportTests</c>.
/// </summary>
internal static class NewRequestTypesSqlScript
{
    public const string FileName = "ImportNewRequestTypes_UAT.sql";
    public const string BeginMarker = "-- <generated-data> (rendered from NewRequestTypesBusinessReview; do not edit by hand)";
    public const string EndMarker = "-- </generated-data>";

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (global.json) not found.");
    }

    public static string RenderDataBlock()
    {
        var rows = NewRequestTypesBusinessReview.Rows();
        var sb = new StringBuilder();
        sb.Append(BeginMarker).Append('\n');

        sb.Append("INSERT INTO @Rows ([Ordinal], [RequestCode], [OwnerDepartmentName], [OwnerDepartmentCode], [Name], [WorkflowName], [WorkflowDescription], [DefaultPriorityId], [AllowReopen], [RequiredFieldsJson], [ResolutionDays]) VALUES\n");
        sb.AppendJoin(",\n", rows.Select((r, i) => string.Join(", ",
            "(" + (i + 1).ToString(CultureInfo.InvariantCulture),
            Literal(r.RequestCode),
            Literal(r.OwningDepartment.Name),
            Literal(r.OwningDepartment.Code),
            Literal(r.Name),
            Literal(r.WorkflowName),
            Literal(r.WorkflowDescription),
            ((byte)r.Priority).ToString(CultureInfo.InvariantCulture),
            r.AllowReopenFlag ? "1" : "0",
            Literal(r.RequiredFieldsJson),
            (r.ResolutionBusinessDays?.ToString(CultureInfo.InvariantCulture) ?? "NULL") + ")")));
        sb.Append(";\n\n");

        sb.Append("INSERT INTO @Steps ([RequestCode], [Sequence], [Name], [Kind], [IsOptional]) VALUES\n");
        sb.AppendJoin(",\n", rows.SelectMany(r => r.Steps.Select((s, i) => string.Join(", ",
            "(" + Literal(r.RequestCode),
            (i + 1).ToString(CultureInfo.InvariantCulture),
            Literal(s.Name),
            ((byte)s.Kind).ToString(CultureInfo.InvariantCulture),
            (s.IsOptional ? "1" : "0") + ")"))));
        sb.Append(";\n\n");

        sb.Append("INSERT INTO @Destinations ([RequestCode], [Ordinal], [DepartmentName], [DepartmentCode]) VALUES\n");
        sb.AppendJoin(",\n", rows.SelectMany(r => r.Destinations.Select((d, i) => string.Join(", ",
            "(" + Literal(r.RequestCode),
            (i + 1).ToString(CultureInfo.InvariantCulture),
            Literal(d.Name),
            Literal(d.Code) + ")"))));
        sb.Append(";\n");

        sb.Append(EndMarker);
        return sb.ToString();
    }

    private static string Literal(string? value) =>
        value is null ? "NULL" : "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
