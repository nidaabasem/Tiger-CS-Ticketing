using TigerCS.Application.Modules.CrmVerification.Dto;
using TigerCS.Application.Modules.CrmVerification.Services;
using TigerCS.Domain.Modules.CrmVerification;
using TigerCS.Tests.CrmVerification.Fakes;

namespace TigerCS.Tests.CrmVerification.Services;

public class VerificationSessionAppServiceTests
{
    private sealed record Fixture(
        VerificationSessionAppService Service,
        FakeUnitReferenceRepository Units,
        FakeContactReferenceRepository Contacts,
        FakeVerificationSessionRepository Sessions,
        FakeAuditEntryWriter Audit);

    private static Fixture CreateService()
    {
        var units = new FakeUnitReferenceRepository();
        var contacts = new FakeContactReferenceRepository();
        var sessions = new FakeVerificationSessionRepository();
        var audit = new FakeAuditEntryWriter();
        var service = new VerificationSessionAppService(
            sessions, units, contacts, new FakeCrmVerificationUnitOfWork(), audit, TimeProvider.System);
        return new Fixture(service, units, contacts, sessions, audit);
    }

    [Fact]
    public async Task CreateAndConfirmAsync_ValidSelection_ReturnsConfirmedSessionWithSnapshot()
    {
        var fixture = CreateService();
        var unit = fixture.Units.Seed("CRM-1", "1204", "Tiger Tower A");
        var contact = fixture.Contacts.Seed(unit.UnitReferenceId, "C-1", "Ahmed Al-Farsi");
        var agentId = Guid.NewGuid();

        var result = await fixture.Service.CreateAndConfirmAsync(
            agentId, new CreateVerificationSessionRequestDto(unit.UnitReferenceId, contact.ContactReferenceId, true), null);

        Assert.Equal(VerificationSessionOutcome.Success, result.Outcome);
        Assert.Equal("Confirmed", result.Response!.Status);
        Assert.True(result.Response.ConfirmedVerbally);
        Assert.Equal("1204", result.Response.SnapshotUnitNumber);
        Assert.Equal("Ahmed Al-Farsi", result.Response.SnapshotContactDisplayName);
        Assert.Equal(agentId, result.Response.AgentEmployeeId);
        Assert.Contains(fixture.Audit.Written, w => w.Action == "ConfirmVerificationSession");
    }

    [Fact]
    public async Task CreateAndConfirmAsync_UnknownUnit_ReturnsUnitOrContactNotFound()
    {
        var fixture = CreateService();
        var contact = fixture.Contacts.Seed(unitReferenceId: 999, "C-1", "Ahmed Al-Farsi");

        var result = await fixture.Service.CreateAndConfirmAsync(
            Guid.NewGuid(), new CreateVerificationSessionRequestDto(999, contact.ContactReferenceId, true), null);

        Assert.Equal(VerificationSessionOutcome.UnitOrContactNotFound, result.Outcome);
    }

    [Fact]
    public async Task CreateAndConfirmAsync_ContactBelongsToDifferentUnit_ReturnsUnitOrContactNotFound()
    {
        var fixture = CreateService();
        var unitA = fixture.Units.Seed("CRM-A", "1204");
        var unitB = fixture.Units.Seed("CRM-B", "0507");
        var contactOfB = fixture.Contacts.Seed(unitB.UnitReferenceId, "C-1", "Someone");

        var result = await fixture.Service.CreateAndConfirmAsync(
            Guid.NewGuid(), new CreateVerificationSessionRequestDto(unitA.UnitReferenceId, contactOfB.ContactReferenceId, true), null);

        Assert.Equal(VerificationSessionOutcome.UnitOrContactNotFound, result.Outcome);
    }

    [Fact]
    public async Task CreateAndConfirmAsync_RepeatedIdempotencyKey_ReturnsSameSessionRatherThanCreatingASecondOne()
    {
        var fixture = CreateService();
        var unit = fixture.Units.Seed("CRM-1", "1204");
        var contact = fixture.Contacts.Seed(unit.UnitReferenceId, "C-1", "Ahmed Al-Farsi");
        var agentId = Guid.NewGuid();
        var request = new CreateVerificationSessionRequestDto(unit.UnitReferenceId, contact.ContactReferenceId, true);

        var first = await fixture.Service.CreateAndConfirmAsync(agentId, request, "double-submit-key-1");
        var second = await fixture.Service.CreateAndConfirmAsync(agentId, request, "double-submit-key-1");

        Assert.Equal(first.Response!.VerificationSessionId, second.Response!.VerificationSessionId);
    }

    [Fact]
    public async Task GetAsync_Owner_ReturnsSession()
    {
        var fixture = CreateService();
        var unit = fixture.Units.Seed("CRM-1", "1204");
        var contact = fixture.Contacts.Seed(unit.UnitReferenceId, "C-1", "Ahmed Al-Farsi");
        var agentId = Guid.NewGuid();
        var created = await fixture.Service.CreateAndConfirmAsync(
            agentId, new CreateVerificationSessionRequestDto(unit.UnitReferenceId, contact.ContactReferenceId, true), null);

        var result = await fixture.Service.GetAsync(created.Response!.VerificationSessionId, agentId);

        Assert.Equal(VerificationSessionOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task GetAsync_DifferentAgent_ReturnsForbidden_SingleAgentOwnershipEnforced()
    {
        var fixture = CreateService();
        var unit = fixture.Units.Seed("CRM-1", "1204");
        var contact = fixture.Contacts.Seed(unit.UnitReferenceId, "C-1", "Ahmed Al-Farsi");
        var owningAgent = Guid.NewGuid();
        var created = await fixture.Service.CreateAndConfirmAsync(
            owningAgent, new CreateVerificationSessionRequestDto(unit.UnitReferenceId, contact.ContactReferenceId, true), null);

        var result = await fixture.Service.GetAsync(created.Response!.VerificationSessionId, Guid.NewGuid());

        Assert.Equal(VerificationSessionOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task GetAsync_UnknownSession_ReturnsNotFound()
    {
        var fixture = CreateService();

        var result = await fixture.Service.GetAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(VerificationSessionOutcome.NotFound, result.Outcome);
    }
}
