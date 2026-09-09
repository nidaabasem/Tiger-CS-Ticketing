using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

public class IntakeRecordAppServiceTests
{
    private static (IntakeRecordAppService Service, FakeIntakeRecordRepository Records, FakeDepartmentRepository Departments, FakeAuditEntryWriter Audit, FakeTicketingUnitOfWork UnitOfWork) CreateService() =>
        CreateServiceWithChannels(new FakeChannelRepository().SeedWellKnown());

    private static (IntakeRecordAppService Service, FakeIntakeRecordRepository Records, FakeDepartmentRepository Departments, FakeAuditEntryWriter Audit, FakeTicketingUnitOfWork UnitOfWork) CreateServiceWithChannels(FakeChannelRepository channels)
    {
        var records = new FakeIntakeRecordRepository();
        var departments = new FakeDepartmentRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var service = new IntakeRecordAppService(records, departments, channels, unitOfWork, audit, TimeProvider.System);
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

    // ---- Channel Management: the channel is configuration, resolved and
    // validated here (the Api re-checks nothing the service does not). ----

    [Fact]
    public async Task CreateAsync_ResolvesTheChannelByNumericId_AndPersistsThatChannelId()
    {
        var (service, records, _, audit, _) = CreateService();

        var result = await service.CreateAsync(
            Guid.NewGuid(), new CreateIntakeRecordRequestDto("6", "+971500000001", null, false, null, null));

        Assert.Equal(IntakeRecordOutcome.Success, result.Outcome);
        var stored = await records.GetByIdAsync(result.Response!.IntakeRecordId);
        Assert.Equal(WellKnownChannels.WhatsApp, stored!.ChannelId);
        Assert.Equal("WHATSAPP", result.Response.ChannelId);
        Assert.Equal("WhatsApp", result.Response.ChannelName);
        Assert.Contains(audit.Entries, w => w.Action == "CreateIntakeRecord" && w.AfterValue!.Contains("ChannelId=6", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_ResolvesTheChannelByCode_CaseInsensitively_ForBackwardCompatibility()
    {
        var (service, records, _, _, _) = CreateService();

        var result = await service.CreateAsync(
            Guid.NewGuid(), new CreateIntakeRecordRequestDto("phone", "+971500000001", null, false, null, null));

        Assert.Equal(IntakeRecordOutcome.Success, result.Outcome);
        Assert.Equal(WellKnownChannels.Phone, (await records.GetByIdAsync(result.Response!.IntakeRecordId))!.ChannelId);
        Assert.Equal("PHONE", result.Response.ChannelId);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("NoSuchChannel")]
    [InlineData("")]
    public async Task CreateAsync_InvalidChannelId_IsRejected_AndNothingIsPersisted(string channelId)
    {
        var (service, records, _, audit, unitOfWork) = CreateService();

        var result = await service.CreateAsync(
            Guid.NewGuid(), new CreateIntakeRecordRequestDto(channelId, "+971500000001", null, false, null, null));

        Assert.Equal(IntakeRecordOutcome.ChannelNotFound, result.Outcome);
        Assert.Null(await records.GetByIdAsync(1));
        Assert.Empty(audit.Written);
        Assert.Equal(0, unitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task CreateAsync_InactiveChannel_IsRejectedForANewTicket()
    {
        var channels = new FakeChannelRepository().SeedWellKnown();
        (await channels.GetByIdAsync(WellKnownChannels.Phone))!.Deactivate();
        var (service, records, _, _, _) = CreateServiceWithChannels(channels);

        var result = await service.CreateAsync(
            Guid.NewGuid(), new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, false, null, null));

        Assert.Equal(IntakeRecordOutcome.ChannelInactive, result.Outcome);
        Assert.Null(await records.GetByIdAsync(1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task CreateAsync_ChannelRequiringPhone_RejectsABlankPhoneNumber(string? phoneNumber)
    {
        var (service, records, _, _, _) = CreateService();

        var result = await service.CreateAsync(
            Guid.NewGuid(), new CreateIntakeRecordRequestDto("Phone", phoneNumber!, null, false, null, null));

        Assert.Equal(IntakeRecordOutcome.PhoneNumberRequired, result.Outcome);
        Assert.Null(await records.GetByIdAsync(1));
    }

    [Fact]
    public async Task CreateAsync_ChannelNotRequiringPhone_AllowsAnEmptyPhoneNumber_StoredAsEmpty()
    {
        var (service, records, _, _, _) = CreateService();

        var result = await service.CreateAsync(
            Guid.NewGuid(), new CreateIntakeRecordRequestDto("WALK_IN_KIOSK", "", null, false, null, null));

        Assert.Equal(IntakeRecordOutcome.Success, result.Outcome);
        var stored = await records.GetByIdAsync(result.Response!.IntakeRecordId);
        Assert.Equal(WellKnownChannels.FaceToFaceKiosk, stored!.ChannelId);
        Assert.Equal(string.Empty, stored.PhoneNumber);
        Assert.Equal(string.Empty, result.Response.PhoneNumber);
    }

    [Fact]
    public async Task CreateAsync_PhoneRequirement_FollowsTheChannelConfiguration_NotItsName()
    {
        // The same channel "Phone", reconfigured by an administrator to not
        // require a phone — the rule reads Channel.RequiresPhone, never the
        // channel's name or code.
        var channels = new FakeChannelRepository();
        channels.AddChannel("Phone", "Phone", requiresPhone: false);
        channels.AddChannel("Face to Face / Kiosk", "FaceToFaceKiosk", requiresPhone: true);
        var (service, _, _, _, _) = CreateServiceWithChannels(channels);

        Assert.Equal(IntakeRecordOutcome.Success,
            (await service.CreateAsync(Guid.NewGuid(), new CreateIntakeRecordRequestDto("Phone", "", null, false, null, null))).Outcome);
        Assert.Equal(IntakeRecordOutcome.PhoneNumberRequired,
            (await service.CreateAsync(Guid.NewGuid(), new CreateIntakeRecordRequestDto("FaceToFaceKiosk", "", null, false, null, null))).Outcome);
    }

    [Fact]
    public async Task CreateAsync_PhoneNumber_TravelsVerbatim_NeverReformatted()
    {
        var (service, records, _, _, _) = CreateService();

        var result = await service.CreateAsync(
            Guid.NewGuid(), new CreateIntakeRecordRequestDto("Phone", "+971 50 123 4567", null, false, null, null));

        Assert.Equal("+971 50 123 4567", (await records.GetByIdAsync(result.Response!.IntakeRecordId))!.PhoneNumber);
    }
}
