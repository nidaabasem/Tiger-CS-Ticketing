namespace TigerCS.Application.Modules.ClassificationAndRouting.Dto;

/// <summary>An active Ticket Category the caller may route a ticket to. <c>GET /api/categories</c>.</summary>
/// <param name="CategoryId">The real database id — the only value <c>POST /api/tickets</c> accepts for <c>CategoryId</c>.</param>
/// <param name="Name">The Category's display name.</param>
/// <param name="DepartmentId">The Department this Category routes to.</param>
/// <param name="DepartmentName">The routed Department's display name, so callers can group/label Categories without a second lookup.</param>
public sealed record CategoryDto(int CategoryId, string Name, int DepartmentId, string DepartmentName);
