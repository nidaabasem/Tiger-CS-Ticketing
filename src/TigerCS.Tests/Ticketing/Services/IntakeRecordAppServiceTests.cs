using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

public class IntakeRecordAppServiceTests
{
    private static (IntakeRecordAppService Service, FakeIntakeRecordRepository Records, FakeAuditEntryWriter Audit) CreateService()
    {
        var records = new FakeIntakeRecordRepository();
        var audit = new FakeAuditEntryWriter();
        var service = new IntakeRecordAppService(records, new FakeTicketingUnitOfWork(), audit, TimeProvider.System);
        return (service, records, audit);
    }

    [Fact]
    public async Task CreateAsync_UnitRelated_PersistsUnverifiedRecordAndAudits()
    {
        var (service, records, audit) = CreateService();
        var employeeId = Guid.NewGuid();

        var result = await service.CreateAsync(
            employeeId, new CreateIntakeRecordRequestDto("Phone", IsUnitRelated: true, "1204", PriorityHint: null));

        Assert.Equal(IntakeRecordOutcome.Success, result.Outcome);
        Assert.True(result.Response!.IsUnitRelated);
        Assert.Equal("Unverified", result.Response.CrmVerificationStatus);
        Assert.NotNull(await records.GetByIdAsync(result.Response.IntakeRecordId));
        Assert.Contains(audit.Written, w => w.Action == "CreateIntakeRecord" && w.ActorEmployeeId == employeeId);
    }

    [Fact]
    public async Task CreateAsync_NonUnitRelated_PersistsWithNoRawUnitNumber()
    {
        var (service, _, _) = CreateService();

        var result = await service.CreateAsync(
            Guid.NewGuid(), new CreateIntakeRecordRequestDto("Phone", IsUnitRelated: false, null, PriorityHint: null));

        Assert.Equal(IntakeRecordOutcome.Success, result.Outcome);
        Assert.False(result.Response!.IsUnitRelated);
        Assert.Null(result.Response.RawUnitNumberEntered);
    }
}
