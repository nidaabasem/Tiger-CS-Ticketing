using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

public sealed class UsersModel(AdminApiClient adminApi) : AdminPageModel
{
    public string? Search { get; private set; }
    public bool IncludeInactive { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int PageSize { get; private set; } = 25;
    public AdminUserListDto? Users { get; private set; }
    public string? LoadError { get; private set; }

    public int TotalPages => Users is null || Users.TotalCount == 0 ? 1 : (int)Math.Ceiling(Users.TotalCount / (double)PageSize);

    public async Task OnGetAsync(string? search, bool includeInactive, int page, CancellationToken cancellationToken)
    {
        Search = search;
        IncludeInactive = includeInactive;
        PageNumber = page < 1 ? 1 : page;

        var result = await adminApi.GetUsersAsync(search, includeInactive, PageNumber, PageSize, cancellationToken);
        if (result.IsSuccess)
        {
            Users = result.Value;
        }
        else
        {
            LoadError = DescribeFailure(result, "The user list could not be loaded.");
        }
    }
}
