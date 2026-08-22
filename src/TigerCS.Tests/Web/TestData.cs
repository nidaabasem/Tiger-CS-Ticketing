using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Tests.Web;

/// <summary>
/// Response bodies for the stubbed-API tests, built from the API's own DTO
/// records so a contract change is a compile error here.
/// </summary>
public static class TestData
{
    public const string RowVersion = "AAAAAAAAB9E=";

    public static LoginResponseDto Login(params string[] roles) => new(
        AccessToken: "header.payload.signature",
        ExpiresAtUtc: DateTime.UtcNow.AddHours(1),
        EmployeeId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        DisplayName: "Test Agent",
        Roles: roles,
        PrimaryDepartmentId: 1);

    public static CurrentUserResponseDto Profile(params string[] roles) => new(
        EmployeeId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        DisplayName: "Test Agent",
        Roles: roles,
        Departments: [new DepartmentMembershipDto(1, "Customer Service", IsPrimary: true)],
        IsGeynessStaff: true);

    public static TicketSummaryDto Summary(
        long ticketId = 1,
        string ticketNumber = "CS-2026-0001",
        byte priorityId = 3,
        string status = "Open",
        string verification = "Verified",
        Guid? owner = null,
        int departmentId = 1) => new(
        TicketId: ticketId,
        TicketNumber: ticketNumber,
        CurrentDepartmentId: departmentId,
        CurrentOwnerEmployeeId: owner,
        CategoryId: 7,
        PriorityId: priorityId,
        TicketStatus: status,
        VerificationStatus: verification,
        RequestSummary: "Air conditioning in the main hall is not cooling.",
        CreatedAtUtc: new DateTime(2026, 8, 20, 9, 15, 0, DateTimeKind.Utc));

    public static TicketListResultDto Queue(int totalCount, params TicketSummaryDto[] items) =>
        new(items, totalCount, 1, 25);

    public static TicketDetailDto Detail(
        long ticketId = 1,
        string ticketNumber = "CS-2026-0001",
        byte priorityId = 3,
        string status = "Open",
        string verification = "Verified",
        Guid? owner = null,
        int departmentId = 1,
        string escalationLevel = "None",
        string slaState = "Running") => new(
        TicketId: ticketId,
        TicketNumber: ticketNumber,
        OriginatingDepartmentId: 1,
        CurrentDepartmentId: departmentId,
        CurrentOwnerEmployeeId: owner,
        UnitReferenceId: verification == "Verified" ? 42 : null,
        ContactReferenceId: verification == "Verified" ? 84 : null,
        CategoryId: 7,
        PriorityId: priorityId,
        TicketStatus: status,
        VerificationStatus: verification,
        EscalationLevel: escalationLevel,
        SlaState: slaState,
        ResolutionOutcome: null,
        DuplicateOfTicketId: null,
        RequestSummary: "Air conditioning in the main hall is not cooling.",
        ReopenCount: 0,
        CreatedAtUtc: new DateTime(2026, 8, 20, 9, 15, 0, DateTimeKind.Utc),
        RowVersion: RowVersion);

    public static TicketSlaSummaryResponseDto Sla(
        string slaState = "Running",
        bool firstResponseBreached = false,
        bool resolutionBreached = false,
        string escalationLevel = "None") => new(
        SlaState: slaState,
        FirstResponseDueAtUtc: new DateTime(2026, 8, 20, 11, 15, 0, DateTimeKind.Utc),
        FirstResponseBreached: firstResponseBreached,
        FirstHumanResponseAtUtc: null,
        ResolutionDueAtUtc: new DateTime(2026, 8, 21, 9, 15, 0, DateTimeKind.Utc),
        ResolutionBreached: resolutionBreached,
        IsCurrentlyPaused: false,
        CurrentPauseReason: null,
        TotalPausedMinutesThisPeriod: 0,
        EscalationLevel: escalationLevel);

    public static TicketNoteListResultDto Notes(params TicketNoteResponseDto[] items) =>
        new(items, items.Length, 1, 100);

    public static PagedResultDto<DepartmentUserDto> Roster(params DepartmentUserDto[] members) =>
        new(members, 1, 100, members.Length);

    public static DepartmentUserDto Member(Guid employeeId, string displayName, params string[] roles) =>
        new(employeeId, displayName, IsPrimary: true, roles);

    public static IntakeRecordResponseDto Intake(
        long intakeRecordId = 500,
        string verificationStatus = "Unverified",
        long? linkedTicketId = null) => new(
        IntakeRecordId: intakeRecordId,
        ChannelId: "Phone",
        ReceivedAtUtc: new DateTime(2026, 8, 22, 8, 0, 0, DateTimeKind.Utc),
        IsUnitRelated: true,
        RawUnitNumberEntered: "A-1402",
        PriorityHint: 3,
        CrmVerificationStatus: verificationStatus,
        LinkedTicketId: linkedTicketId);

    public static UnitVerificationResponseDto Unit() => new(
        UnitReferenceId: 42,
        CrmUnitId: "CRM-UNIT-42",
        UnitNumber: "A-1402",
        PropertyName: "Tiger Tower",
        TowerName: "Tower A",
        UnitType: "Apartment",
        LastSyncedAtUtc: new DateTime(2026, 8, 22, 7, 0, 0, DateTimeKind.Utc),
        ContactCount: 2);

    public static ContactVerificationResponseDto Contact(
        int contactReferenceId = 84,
        string displayName = "Mona Haddad",
        string contactType = "Owner",
        int? representativeOf = null) => new(
        ContactReferenceId: contactReferenceId,
        CrmContactId: "CRM-CONTACT-84",
        DisplayName: displayName,
        ContactChannel: "+971 50 000 0000",
        ContactType: contactType,
        AuthorizedRepresentativeOfContactReferenceId: representativeOf);

    public static VerificationSessionResponseDto Session(Guid? sessionId = null) => new(
        VerificationSessionId: sessionId ?? Guid.Parse("22222222-2222-2222-2222-222222222222"),
        AgentEmployeeId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        UnitReferenceId: 42,
        ContactReferenceId: 84,
        Status: "Confirmed",
        Confirmed: true,
        VerificationMethod: "ManualAgentConfirmation",
        CreatedAtUtc: new DateTime(2026, 8, 22, 8, 1, 0, DateTimeKind.Utc),
        ConfirmedAtUtc: new DateTime(2026, 8, 22, 8, 2, 0, DateTimeKind.Utc),
        ExpiresAtUtc: DateTime.UtcNow.AddMinutes(30),
        SnapshotUnitNumber: "A-1402",
        SnapshotPropertyName: "Tiger Tower",
        SnapshotTowerName: "Tower A",
        SnapshotUnitType: "Apartment",
        SnapshotContactDisplayName: "Mona Haddad",
        SnapshotContactChannel: "+971 50 000 0000");

    public static TicketResponseDto Created(
        string ticketNumber = "CS-2026-0777",
        byte priorityId = 3,
        string verification = "Verified") => new(
        TicketId: 777,
        TicketNumber: ticketNumber,
        OriginatingDepartmentId: 1,
        CurrentDepartmentId: 1,
        UnitReferenceId: verification == "Verified" ? 42 : null,
        ContactReferenceId: verification == "Verified" ? 84 : null,
        CategoryId: 7,
        PriorityId: priorityId,
        TicketStatus: "Open",
        VerificationStatus: verification,
        EscalationLevel: "None",
        SlaState: "Running",
        RequestSummary: "Air conditioning in the main hall is not cooling.",
        CreatedAtUtc: new DateTime(2026, 8, 22, 8, 5, 0, DateTimeKind.Utc),
        RowVersion: RowVersion);
}
