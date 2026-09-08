namespace TigerCS.Application.Modules.Administration.Dto;

public sealed record AdminDepartmentDto(
    int DepartmentId,
    string Name,
    string Code,
    bool IsActive,
    int MemberCount,
    int TicketReferenceCount,
    int RequestTypeCount);

public sealed record DepartmentMemberDto(
    Guid EmployeeId,
    string DisplayName,
    bool IsPrimary,
    bool IsActive,
    IReadOnlyList<string> Roles);

public sealed record AdminDepartmentDetailDto(
    int DepartmentId,
    string Name,
    string Code,
    bool IsActive,
    int TicketReferenceCount,
    int RequestTypeCount,
    IReadOnlyList<DepartmentMemberDto> Members);

public sealed record SaveDepartmentRequestDto(string Name, string Code);

public sealed record AddDepartmentMemberRequestDto(Guid EmployeeId, bool IsPrimary);
