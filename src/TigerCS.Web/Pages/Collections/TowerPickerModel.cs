using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Web.Pages.Collections;

public sealed record TowerPickerModel(string Id, IReadOnlyList<CollectionsTowerDto> Towers, int? SelectedTowerId, bool LoadFailed = false);
