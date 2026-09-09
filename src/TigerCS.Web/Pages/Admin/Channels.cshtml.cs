using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

/// <summary>
/// Admin → Configuration → Channels: the configured channel catalogue the
/// Create Ticket wizard reads its Channel picker from. Add here; edit and
/// activate/deactivate on the channel's own page. Channels are never
/// deleted — an inactive one keeps its name on every historical intake
/// record and interaction and simply stops being offered for new tickets.
/// </summary>
public sealed class ChannelsModel(AdminApiClient adminApi) : AdminPageModel
{
    public IReadOnlyList<AdminChannelDto> Channels { get; private set; } = [];
    public bool IncludeInactive { get; private set; } = true;
    public string? LoadError { get; private set; }

    [BindProperty] public CreateInput Create { get; set; } = new();

    public async Task OnGetAsync(bool? includeInactive, CancellationToken cancellationToken)
    {
        IncludeInactive = includeInactive ?? true;
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ErrorMessage = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            await LoadAsync(cancellationToken);
            return Page();
        }

        var result = await adminApi.CreateChannelAsync(
            new SaveChannelRequestDto(Create.Name, Create.Code, Create.RequiresPhone, Create.IsGenesysEnabled, Create.DisplayOrder, Create.IsActive),
            cancellationToken);
        if (result.IsSuccess)
        {
            StatusMessage = $"Channel '{result.Value!.Name}' created.";
            return RedirectToPage("/Admin/Channels");
        }

        ErrorMessage = DescribeFailure(result, "The channel could not be created.");
        await LoadAsync(cancellationToken);
        return Page();
    }

    /// <summary>One-click Activate / Deactivate from the list's Actions column.</summary>
    public async Task<IActionResult> OnPostActivationAsync(byte id, bool isActive, CancellationToken cancellationToken)
    {
        var result = await adminApi.SetChannelActivationAsync(id, new SetActiveRequestDto(isActive), cancellationToken);
        if (result.IsSuccess)
        {
            StatusMessage = isActive
                ? $"Channel '{result.Value!.Name}' activated."
                : $"Channel '{result.Value!.Name}' deactivated. Historical tickets keep its name.";
        }
        else
        {
            ErrorMessage = DescribeFailure(result, "The channel's status could not be changed.");
        }

        return RedirectToPage("/Admin/Channels");
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var result = await adminApi.GetChannelsAsync(IncludeInactive, cancellationToken);
        if (result.IsSuccess)
        {
            Channels = result.Value ?? [];
        }
        else
        {
            LoadError = DescribeFailure(result, "The channel list could not be loaded.");
        }
    }

    public sealed class CreateInput
    {
        [Required(ErrorMessage = "Name is required.")] public string Name { get; set; } = string.Empty;
        [Required(ErrorMessage = "Code is required.")] public string Code { get; set; } = string.Empty;
        public bool RequiresPhone { get; set; } = true;
        public bool IsGenesysEnabled { get; set; }
        public bool IsActive { get; set; } = true;
        [Range(0, int.MaxValue, ErrorMessage = "Display order must be zero or a positive whole number.")] public int DisplayOrder { get; set; }
    }
}
