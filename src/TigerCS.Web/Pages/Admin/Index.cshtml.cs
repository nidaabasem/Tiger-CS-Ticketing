using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

public sealed record AdminAreaCard(string Title, string Description, string Href, int? Count, string CountLabel, string Icon);

public sealed class IndexModel(AdminApiClient adminApi) : AdminPageModel
{
    public IReadOnlyList<AdminAreaCard> Cards { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var users = adminApi.GetUsersAsync(null, includeInactive: false, 1, 1, cancellationToken);
        var departments = adminApi.GetDepartmentsAsync(includeInactive: false, cancellationToken);
        var requestTypes = adminApi.GetRequestTypesAsync(null, includeInactive: false, cancellationToken);
        var workflows = adminApi.GetWorkflowsAsync(includeInactive: false, cancellationToken);
        await Task.WhenAll(users, departments, requestTypes, workflows);

        Cards =
        [
            new("Users", "Accounts, roles and department membership. Users are deactivated, never deleted.", "/Admin/Users",
                users.Result.IsSuccess ? users.Result.Value?.TotalCount : null, "active users", "users"),
            new("Departments", "Responsible departments and their members.", "/Admin/Departments",
                departments.Result.IsSuccess ? departments.Result.Value?.Count : null, "active departments", "departments"),
            new("Request Types", "What each department handles, how it is assigned, what approvals and SLAs apply.", "/Admin/RequestTypes",
                requestTypes.Result.IsSuccess ? requestTypes.Result.Value?.Count : null, "active request types", "request-types"),
            new("Workflows", "The versioned step sequences request types follow — Draft, Active, Historical.", "/Admin/Workflows",
                workflows.Result.IsSuccess ? workflows.Result.Value?.Count : null, "active workflows", "workflows")
        ];
    }
}
