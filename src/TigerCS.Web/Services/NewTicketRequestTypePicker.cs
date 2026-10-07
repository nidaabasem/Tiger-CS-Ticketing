using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;

namespace TigerCS.Web.Services;

/// <summary>The existing department groups are Request Categories in intake.
/// A child retains its routing category and, when configured, its workflow request type.</summary>
public sealed record NewTicketRequestTypeChoice(
    string Value, string Name, int DepartmentId, int? CategoryId, int? RequestTypeId);

public static class NewTicketRequestTypePicker
{
    public static IReadOnlyList<NewTicketRequestTypeChoice> Build(
        int? departmentId, IEnumerable<CategoryDto> categories, IEnumerable<RequestTypeOptionDto> requestTypes)
    {
        if (departmentId is null)
        {
            return [];
        }

        var routing = categories.Where(c => c.DepartmentId == departmentId).ToList();
        var configured = requestTypes.Where(r => r.DepartmentId == departmentId).ToList();
        var choices = new List<NewTicketRequestTypeChoice>();
        foreach (var requestType in configured.Where(r => r.HasPublishedWorkflow))
        {
            // Only an exact, unique mapping is accepted. Missing/inactive/ambiguous
            // routing is a configuration error, never an arbitrary department category.
            var matches = routing.Where(c => SameName(c.Name, requestType.Name)).ToList();
            choices.Add(new($"request-type:{requestType.RequestTypeId}", requestType.Name,
                requestType.DepartmentId, matches.Count == 1 ? matches[0].CategoryId : null, requestType.RequestTypeId));
        }

        foreach (var category in routing.Where(c => !configured.Any(r => SameName(r.Name, c.Name))))
        {
            choices.Add(new($"category:{category.CategoryId}", category.Name,
                category.DepartmentId, category.CategoryId, null));
        }

        return choices.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Value).ToList();
    }

    private static bool SameName(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
