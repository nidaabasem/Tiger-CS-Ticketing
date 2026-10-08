using TigerCS.Api.Controllers;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The Genesys-facing contract names enum members with words. An internal enum number ("1") must not be accepted in their place:
/// it silently means something else after the enum is reordered, and numbers outside the enum used to be stored as is.
/// </summary>
public sealed class NamedEnumWireContractTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    [Theory]
    [InlineData("Phone", true)]
    [InlineData("phone", true)]
    [InlineData(" WebsiteChat ", true)]
    [InlineData("1", false)]
    [InlineData("2", false)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("99", false)]
    [InlineData("Phone, WhatsApp", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void NamedEnum_AcceptsOnlyMemberNames(string? value, bool expected)
    {
        Assert.Equal(expected, NamedEnum.TryParse<GenesysChannel>(value, out _));
    }

    [Theory]
    [InlineData("Phone", true)]
    [InlineData("LiveChat", true)]      // documented Genesys alias
    [InlineData("WebMessaging", true)]  // documented Genesys alias
    [InlineData("1", false)]
    [InlineData("3", false)]
    [InlineData("99", false)]
    public void TheTicketIngestionChannel_IsAWord_NeverAnEnumNumber(string value, bool expected)
    {
        Assert.Equal(expected, GenesysContractMapper.TryParseChannel(value, out _));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("99")]
    [InlineData("-1")]
    public async Task HandoffMode_AsANumber_IsInvalidMode_AndNothingIsStored(string mode)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var ingested = await f.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto("conv-num-mode", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: department.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, ingested.Outcome);

        var result = await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("conv-num-mode", Mode: mode));

        Assert.Equal(GenesysHandoffOutcome.InvalidMode, result.Outcome);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("99")]
    public async Task HandoffTrigger_AsANumber_IsInvalidTrigger(string trigger)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        await f.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto("conv-num-trigger", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: department.DepartmentId));

        var result = await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("conv-num-trigger", Trigger: trigger));

        Assert.Equal(GenesysHandoffOutcome.InvalidTrigger, result.Outcome);
    }

    [Fact]
    public async Task HandoffMode_AndTrigger_ByName_StillWork()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        await f.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto("conv-named", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: department.DepartmentId));

        var result = await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("conv-named", Mode: "ContinueChat", Trigger: "CustomerRequestedHuman"));

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, result.Outcome);
    }
}
