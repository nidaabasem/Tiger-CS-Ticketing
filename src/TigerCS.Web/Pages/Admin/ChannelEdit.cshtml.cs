using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

public sealed class ChannelEditModel(AdminApiClient adminApi) : AdminPageModel
{
    public byte ChannelId { get; private set; }
    public AdminChannelDto? Channel { get; private set; }
    public string? LoadError { get; private set; }

    [BindProperty] public EditInput Edit { get; set; } = new();
    [BindProperty] public ActivationInput Activation { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(byte id, CancellationToken cancellationToken)
    {
        ChannelId = id;
        var result = await adminApi.GetChannelAsync(id, cancellationToken);
        if (result.Outcome == ApiOutcome.NotFound)
        {
            return NotFound();
        }

        if (!result.IsSuccess)
        {
            LoadError = DescribeFailure(result, "The channel could not be loaded.");
            return Page();
        }

        Channel = result.Value;
        Edit = new EditInput
        {
            Name = Channel!.Name,
            Code = Channel.Code,
            RequiresPhone = Channel.RequiresPhone,
            IsGenesysEnabled = Channel.IsGenesysEnabled,
            IsActive = Channel.IsActive,
            DisplayOrder = Channel.DisplayOrder
        };
        return Page();
    }

    public async Task<IActionResult> OnPostEditAsync(byte id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ErrorMessage = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToPage("/Admin/ChannelEdit", new { id });
        }

        return await ApplyAsync(id,
            adminApi.UpdateChannelAsync(id,
                new SaveChannelRequestDto(Edit.Name, Edit.Code, Edit.RequiresPhone, Edit.IsGenesysEnabled, Edit.DisplayOrder, Edit.IsActive),
                cancellationToken),
            "Channel saved.", "The channel could not be saved.");
    }

    public Task<IActionResult> OnPostActivationAsync(byte id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.SetChannelActivationAsync(id, new SetActiveRequestDto(Activation.IsActive, Activation.Reason), cancellationToken),
            Activation.IsActive ? "Channel activated." : "Channel deactivated. Historical tickets keep its name.",
            "The channel's status could not be changed.");

    private async Task<IActionResult> ApplyAsync(byte id, Task<ApiResult<AdminChannelDto>> call, string success, string failure)
    {
        var result = await call;
        if (result.IsSuccess)
        {
            StatusMessage = success;
        }
        else
        {
            ErrorMessage = DescribeFailure(result, failure);
        }

        return RedirectToPage("/Admin/ChannelEdit", new { id });
    }

    public sealed class EditInput
    {
        [Required(ErrorMessage = "Name is required.")] public string Name { get; set; } = string.Empty;
        [Required(ErrorMessage = "Code is required.")] public string Code { get; set; } = string.Empty;
        public bool RequiresPhone { get; set; }
        public bool IsGenesysEnabled { get; set; }
        public bool IsActive { get; set; }
        [Range(0, int.MaxValue, ErrorMessage = "Display order must be zero or a positive whole number.")] public int DisplayOrder { get; set; }
    }

    public sealed class ActivationInput
    {
        public bool IsActive { get; set; }
        public string? Reason { get; set; }
    }
}
