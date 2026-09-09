using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Administration.Services;

/// <summary>
/// Channel Management (Admin → Configuration → Channels): list, add, edit,
/// activate/deactivate, code uniqueness — and the regression rule that a
/// deactivated channel keeps resolving for every historical reference while
/// disappearing from the new-ticket directory.
/// </summary>
public class AdminChannelAppServiceTests
{
    private static readonly Guid Admin = Guid.NewGuid();

    private sealed record Fixture(
        AdminChannelAppService Service,
        FakeChannelRepository Channels,
        FakeAuditEntryWriter Audit,
        ChannelDirectoryAppService Directory);

    private static Fixture Create(bool seedWellKnown = true)
    {
        var channels = new FakeChannelRepository();
        if (seedWellKnown)
        {
            channels.SeedWellKnown();
        }

        var audit = new FakeAuditEntryWriter();
        var service = new AdminChannelAppService(channels, new FakeTicketingUnitOfWork(), audit);
        return new Fixture(service, channels, audit, new ChannelDirectoryAppService(channels));
    }

    [Fact]
    public async Task List_ShowsEveryChannel_WithItsActiveStatus_OrderedByDisplayOrderThenName()
    {
        var f = Create();
        f.Channels.AddChannel("Zeta", "ZETA", displayOrder: 0, isActive: false);
        f.Channels.AddChannel("Alpha", "ALPHA", displayOrder: 0);

        var all = await f.Service.ListAsync(includeInactive: true);

        Assert.Equal(
            [
                "Alpha", "Zeta", "Phone", "WhatsApp", "Live Chat", "Social Media Direct Message",
                "Website", "Walk in / Kiosk", "Mobile App (Customer Portal)", "Instagram", "Facebook",
                "App / Website (Legacy)", "WhatsApp / Live Chat (Legacy)"
            ],
            all.Select(c => c.Name).ToArray());
        Assert.False(all.Single(c => c.Code == "ZETA").IsActive);
        Assert.True(all.Single(c => c.Code == "ALPHA").IsActive);

        var activeOnly = await f.Service.ListAsync(includeInactive: false);
        Assert.DoesNotContain(activeOnly, c => c.Code == "ZETA");
        // Alpha + the nine approved active channels (the two legacy rows and Zeta are inactive).
        Assert.Equal(10, activeOnly.Count);
    }

    [Fact]
    public async Task Create_AddsTheChannel_WithItsConfiguration_AndAudits()
    {
        var f = Create();

        var created = await f.Service.CreateAsync(Admin,
            new SaveChannelRequestDto(" Telegram ", " TELEGRAM ", RequiresPhone: true, IsGenesysEnabled: true, DisplayOrder: 7));

        Assert.Equal(AdminOutcome.Success, created.Outcome);
        Assert.Equal("Telegram", created.Value!.Name);
        Assert.Equal("TELEGRAM", created.Value.Code);
        Assert.True(created.Value.RequiresPhone);
        Assert.True(created.Value.IsGenesysEnabled);
        Assert.True(created.Value.IsActive);
        Assert.Equal(7, created.Value.DisplayOrder);
        Assert.Equal(0, created.Value.ReferenceCount);
        Assert.Contains(f.Audit.Written, w => w.Action == "AdminCreateChannel" && w.ActorEmployeeId == Admin);

        // Offered to Create Ticket immediately, in display order.
        Assert.Contains(await f.Directory.ListAsync(activeOnly: true), c => c.ChannelId == created.Value.ChannelId);
    }

    [Fact]
    public async Task Create_WithoutNameOrCode_IsRejected_WithModelStateStyleErrors()
    {
        var f = Create();

        var result = await f.Service.CreateAsync(Admin, new SaveChannelRequestDto("", " ", false, false, 0));

        Assert.Equal(AdminOutcome.ValidationFailed, result.Outcome);
        Assert.Contains("Name is required.", result.Errors!);
        Assert.Contains("Code is required.", result.Errors!);
        Assert.Equal(11, f.Channels.All.Count);
    }

    [Fact]
    public async Task Create_NegativeDisplayOrder_IsRejected()
    {
        var f = Create();

        var result = await f.Service.CreateAsync(Admin, new SaveChannelRequestDto("Email", "EMAIL", false, false, -1));

        Assert.Equal(AdminOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(result.Errors!, e => e.Contains("Display order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateCode_IsRejected_CaseInsensitively_OnCreateAndEdit()
    {
        var f = Create();

        var duplicate = await f.Service.CreateAsync(Admin, new SaveChannelRequestDto("Telephone", "phone", true, true, 9));
        Assert.Equal(AdminOutcome.ValidationFailed, duplicate.Outcome);
        Assert.Contains(duplicate.Errors!, e => e.Contains("already exists", StringComparison.Ordinal));

        var edited = await f.Service.UpdateAsync(Admin, WellKnownChannels.AppOrWebsite,
            new SaveChannelRequestDto("App", "PHONE", true, false, 2));
        Assert.Equal(AdminOutcome.ValidationFailed, edited.Outcome);

        // Editing a channel keeping its own code is not a duplicate of itself.
        var unchanged = await f.Service.UpdateAsync(Admin, WellKnownChannels.Phone,
            new SaveChannelRequestDto("Phone Call", "Phone", true, true, 1));
        Assert.Equal(AdminOutcome.Success, unchanged.Outcome);
        Assert.Equal("Phone Call", unchanged.Value!.Name);
    }

    [Fact]
    public async Task Edit_ChangesEveryField_AndHistoryResolvesTheNewNameByTheSameId()
    {
        var f = Create();
        f.Channels.References[WellKnownChannels.FaceToFaceKiosk] = 12;

        var edited = await f.Service.UpdateAsync(Admin, WellKnownChannels.FaceToFaceKiosk,
            new SaveChannelRequestDto("Walk-in / Kiosk", "KIOSK", RequiresPhone: true, IsGenesysEnabled: true, DisplayOrder: 0, IsActive: false));

        Assert.Equal(AdminOutcome.Success, edited.Outcome);
        Assert.Equal("Walk-in / Kiosk", edited.Value!.Name);
        Assert.Equal("KIOSK", edited.Value.Code);
        Assert.True(edited.Value.RequiresPhone);
        Assert.True(edited.Value.IsGenesysEnabled);
        Assert.False(edited.Value.IsActive);
        Assert.Equal(0, edited.Value.DisplayOrder);
        Assert.Equal(12, edited.Value.ReferenceCount);
        Assert.Contains(f.Audit.Written, w => w.Action == "AdminUpdateChannel");

        // The identity is the id: a historical record's channel resolves to the new name.
        Assert.Equal("Walk-in / Kiosk", (await f.Channels.GetByIdAsync(WellKnownChannels.FaceToFaceKiosk))!.Name);
    }

    [Fact]
    public async Task Edit_UnknownChannel_IsNotFound()
    {
        var f = Create();

        var result = await f.Service.UpdateAsync(Admin, 200, new SaveChannelRequestDto("X", "X", false, false, 0));

        Assert.Equal(AdminOutcome.NotFound, result.Outcome);
        Assert.Equal(AdminOutcome.NotFound, (await f.Service.SetActivationAsync(Admin, 200, new SetActiveRequestDto(false))).Outcome);
        Assert.Null(await f.Service.GetAsync(200));
    }

    [Fact]
    public async Task Deactivate_KeepsHistoricalReferencesResolvable_AndHidesTheChannelFromNewTicketsOnly()
    {
        var f = Create();
        f.Channels.References[WellKnownChannels.Phone] = 42;

        var deactivated = await f.Service.SetActivationAsync(Admin, WellKnownChannels.Phone, new SetActiveRequestDto(false, "retired"));

        Assert.Equal(AdminOutcome.Success, deactivated.Outcome);
        Assert.False(deactivated.Value!.IsActive);
        Assert.Equal(42, deactivated.Value.ReferenceCount);
        Assert.Contains(f.Audit.Entries, w => w.Action == "AdminDeactivateChannel" && w.AfterValue!.Contains("Reason=retired", StringComparison.Ordinal));

        // Not deleted: still there, still named, for every historical record.
        var stillThere = await f.Channels.GetByIdAsync(WellKnownChannels.Phone);
        Assert.NotNull(stillThere);
        Assert.Equal("Phone", stillThere.Name);
        Assert.Equal(11, f.Channels.All.Count);

        // Gone from the new-ticket directory only.
        Assert.DoesNotContain(await f.Directory.ListAsync(activeOnly: true), c => c.ChannelId == WellKnownChannels.Phone);
        Assert.Contains(await f.Directory.ListAsync(activeOnly: false), c => c.ChannelId == WellKnownChannels.Phone);
        Assert.Contains(await f.Service.ListAsync(includeInactive: true), c => c.ChannelId == WellKnownChannels.Phone && !c.IsActive);

        var reactivated = await f.Service.SetActivationAsync(Admin, WellKnownChannels.Phone, new SetActiveRequestDto(true));
        Assert.True(reactivated.Value!.IsActive);
        Assert.Contains(await f.Directory.ListAsync(activeOnly: true), c => c.ChannelId == WellKnownChannels.Phone);
    }

    [Fact]
    public async Task Directory_OrdersByDisplayOrderThenName_AndExposesRequiresPhoneAndGenesysConfiguration()
    {
        var f = Create(seedWellKnown: false);
        f.Channels.AddChannel("Kiosk", "KIOSK", requiresPhone: false, isGenesysEnabled: false, displayOrder: 2);
        f.Channels.AddChannel("Phone", "PHONE", requiresPhone: true, isGenesysEnabled: true, displayOrder: 1);
        f.Channels.AddChannel("Email", "EMAIL", requiresPhone: false, isGenesysEnabled: false, displayOrder: 2);
        f.Channels.AddChannel("Fax", "FAX", requiresPhone: true, isGenesysEnabled: false, displayOrder: 3, isActive: false);

        var directory = await f.Directory.ListAsync(activeOnly: true);

        Assert.Equal(["Phone", "Email", "Kiosk"], directory.Select(c => c.Name).ToArray());
        Assert.True(directory[0].RequiresPhone);
        Assert.True(directory[0].IsGenesysEnabled);
        Assert.False(directory[2].RequiresPhone);
        Assert.False(directory[2].IsGenesysEnabled);
    }
}
