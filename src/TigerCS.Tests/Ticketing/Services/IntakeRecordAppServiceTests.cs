using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

public class IntakeRecordAppServiceTests
{
    private static (IntakeRecordAppService Service, FakeIntakeRecordRepository Records, FakeDepartmentRepository Departments, FakeAuditEntryWriter Audit, FakeTicketingUnitOfWork UnitOfWork) CreateService()
    {
        var records = new FakeIntakeRecordRepository();
        var departments = new FakeDepartmentRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var service = new IntakeRecordAppService(records, departments, unitOfWork, audit, TimeProvider.System);
        return (service, records, departments, audit, unitOfWork);
    }

    [Fact]
    public async Task CreateAsync_UnitRelated_PersistsUnverifiedRecordAndAudits()
    {
        var (service, records, _, audit, unitOfWork) = CreateService();
        var employeeId = Guid.NewGuid();

        var result = await service.CreateAsync(
            employeeId, new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, IsUnitRelated: true, "1204", PriorityHint: null));

        Assert.Equal(IntakeRecordOutcome.Success, result.Outcome);
        Assert.Equal("+971500000001", result.Response!.PhoneNumber);
        Assert.True(result.Response.IsUnitRelated);
        Assert.Equal("Unverified", result.Response.CrmVerificationStatus);
        Assert.NotNull(await records.GetByIdAsync(result.Response.IntakeRecordId));
        Assert.Contains(audit.Written, w => w.Action == "CreateIntakeRecord" && w.ActorEmployeeId == employeeId);

        // Senior review item 11: the insert and its audit entry commit as one transaction.
        Assert.Equal(1, unitOfWork.TransactionsBegun);
        Assert.Equal(1, unitOfWork.TransactionsCommitted);
        Assert.Equal(0, unitOfWork.TransactionsRolledBack);
    }

    [Fact]
    public async Task CreateAsync_NonUnitRelated_PersistsWithNoRawUnitNumber()
    {
        var (service, _, _, _, _) = CreateService();

        var result = await service.CreateAsync(
            Guid.NewGuid(), new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, IsUnitRelated: false, null, PriorityHint: null));

        Assert.Equal(IntakeRecordOutcome.Success, result.Outcome);
        Assert.False(result.Response!.IsUnitRelated);
        Assert.Null(result.Response.RawUnitNumberEntered);
    }

    [Fact]
    public async Task CreateAsync_DepartmentIdOmitted_Succeeds()
    {
        var (service, _, _, _, _) = CreateService();

        var result = await service.CreateAsync(
            Guid.NewGuid(), new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, IsUnitRelated: false, null, PriorityHint: null));

        Assert.Equal(IntakeRecordOutcome.Success, result.Outcome);
        Assert.Null(result.Response!.DepartmentId);
    }

    [Fact]
    public async Task CreateAsync_UnknownDepartmentId_ReturnsDepartmentNotFound()
    {
        var (service, _, _, _, _) = CreateService();

        var result = await service.CreateAsync(
            Guid.NewGuid(), new CreateIntakeRecordRequestDto("Phone", "+971500000001", 999, IsUnitRelated: false, null, PriorityHint: null));

        Assert.Equal(IntakeRecordOutcome.DepartmentNotFound, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_KnownDepartmentId_PersistsDepartmentId()
    {
        var (service, records, departments, _, _) = CreateService();
        var department = departments.AddDepartment("Customer Service", "CS");

        var result = await service.CreateAsync(
            Guid.NewGuid(), new CreateIntakeRecordRequestDto("Phone", "+971500000001", department.DepartmentId, IsUnitRelated: false, null, PriorityHint: null));

        Assert.Equal(IntakeRecordOutcome.Success, result.Outcome);
        Assert.Equal(department.DepartmentId, result.Response!.DepartmentId);
        var stored = await records.GetByIdAsync(result.Response.IntakeRecordId);
        Assert.Equal(department.DepartmentId, stored!.DepartmentId);
    }
}
