using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Web.Models;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

public sealed class RequestTypesModel(AdminApiClient adminApi, DepartmentsApiClient departmentsApi) : AdminPageModel
{
    public int? DepartmentId { get; private set; }
    public bool IncludeInactive { get; private set; } = true;
    public IReadOnlyList<AdminRequestTypeSummaryDto> RequestTypes { get; private set; } = [];
    public IReadOnlyCollection<DepartmentDto> Departments { get; private set; } = [];
    public string? LoadError { get; private set; }

    public static string PriorityLabel(byte priorityId) => TicketDisplay.PriorityLabel(priorityId);

    public async Task OnGetAsync(int? departmentId, bool? includeInactive, CancellationToken cancellationToken)
    {
        DepartmentId = departmentId;
        IncludeInactive = includeInactive ?? true;

        var list = adminApi.GetRequestTypesAsync(departmentId, IncludeInactive, cancellationToken);
        var departments = departmentsApi.GetDepartmentsAsync(activeOnly: false, cancellationToken);
        await Task.WhenAll(list, departments);

        Departments = departments.Result.IsSuccess ? departments.Result.Value ?? [] : [];
        if (list.Result.IsSuccess)
        {
            RequestTypes = list.Result.Value ?? [];
        }
        else
        {
            LoadError = DescribeFailure(list.Result, "The request type list could not be loaded.");
        }
    }
}
