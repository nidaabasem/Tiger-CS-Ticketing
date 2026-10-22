using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>The Genesys campaign carries the exact stage name; an existing campaign is reused, never duplicated.</summary>
public sealed class ReviewCampaignNameTests
{
    private const string StageName = "Current month reminder - day 14";
    private const string CurrentMonthList = "79e5ae74-ea6e-4941-b76d-45ddf487d8d1";

    private static async Task<ReviewHarness> SeededAsync(Action<GenesysCampaignOptions>? configure = null, bool enabled = true)
    {
        var h = new ReviewHarness(false);
        h.Genesys.Campaign.Enabled = enabled;
        h.Genesys.Campaign.AllowCreate = true;
        h.Genesys.Campaign.QueueId = "queue-1"; h.Genesys.Campaign.DialingMode = "agentless";
        h.Genesys.Campaign.CallerName = "Tiger"; h.Genesys.Campaign.CallerAddress = "+971400000000";
        configure?.Invoke(h.Genesys.Campaign);
        for (var n = 1; n <= 3; n++) h.Source.Items.Add(ReviewHarness.Row(n, 100m + n));
        await h.RefreshAsync();
        return h;
    }

    private static async Task<DispatchDto> RunAsync(ReviewHarness h, string key = "k1")
    {
        var (_, confirm) = await h.PrepareAsync(null, key);
        using var scope = h.NewScope();
        var accepted = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Accepted, accepted.Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Last(), CancellationToken.None);
        return (await scope.Dispatch.GetAsync(h.Manager, accepted.Value!.DispatchId, CancellationToken.None)).Value!;
    }

    [Theory]
    [InlineData(CampaignReminderType.CurrentMonth, "Current month reminder - day 14")]
    [InlineData(CampaignReminderType.FollowUp, "Follow-up reminder - day 28")]
    [InlineData(CampaignReminderType.Overdue, "Overdue reminder - day 1")]
    [InlineData(CampaignReminderType.LegalNotice, "Legal notice - days 12 and 14")]
    [InlineData(CampaignReminderType.LegalCase, "Internal Legal referral - day 30")]
    public void StageNamesAreExactAndMatchTheStageEnum(CampaignReminderType type, string expected)
    {
        Assert.Equal(expected, CampaignReminderTypes.StageName(type));
        Assert.Equal(expected, CampaignReminderTypes.StageName(CampaignReminderTypes.ToStage(type)));
    }

    [Fact]
    public async Task MissingCampaignIsCreatedWithTheExactStageName_AndRecordedOnTheBatch()
    {
        using var h = await SeededAsync();
        var dispatch = await RunAsync(h);

        var create = Assert.Single(h.Client.CampaignCreates);
        Assert.Equal((StageName, CurrentMonthList), create);
        var batch = Assert.Single(dispatch.Batches);
        Assert.Equal(StageName, batch.StageName);
        Assert.Equal(StageName, batch.GenesysCampaignName);
        Assert.Equal("campaign-1", batch.GenesysCampaignId);
        Assert.Equal("Created", batch.CampaignAction);
        Assert.Equal("Uploaded", batch.Status);
        Assert.Equal("CurrentMonth", batch.ReminderType);          // the internal stage id is untouched
        Assert.Equal(3, Assert.Single(h.Client.Calls).Contacts.Count);
    }

    [Fact]
    public async Task ExistingCampaignWithTheSameNameIsReused_NotDuplicated()
    {
        using var h = await SeededAsync();
        h.Client.Campaigns.Add(new("existing-1", StageName, CurrentMonthList, "off"));
        var dispatch = await RunAsync(h);

        Assert.Empty(h.Client.CampaignCreates);
        var batch = Assert.Single(dispatch.Batches);
        Assert.Equal("existing-1", batch.GenesysCampaignId);
        Assert.Equal("ReusedExisting", batch.CampaignAction);
        Assert.Equal("Uploaded", batch.Status);
    }

    [Fact]
    public async Task ExistingCampaignOnAnotherContactList_FailsSafelyBeforeAnyUpload()
    {
        using var h = await SeededAsync();
        h.Client.Campaigns.Add(new("existing-1", StageName, "00000000-0000-0000-0000-000000000001", "on"));
        var dispatch = await RunAsync(h);

        Assert.Empty(h.Client.CampaignCreates);
        Assert.Empty(h.Client.Calls);
        var batch = Assert.Single(dispatch.Batches);
        Assert.Equal("Failed", batch.Status);
        Assert.Contains("different contact list", batch.Error);
        Assert.Null(batch.GenesysCampaignId);
    }

    [Fact]
    public async Task SeveralCampaignsWithTheName_AreAmbiguous_AndNothingIsUploaded()
    {
        using var h = await SeededAsync();
        h.Client.Campaigns.Add(new("a", StageName, CurrentMonthList, "off"));
        h.Client.Campaigns.Add(new("b", StageName.ToUpperInvariant(), CurrentMonthList, "off"));
        var dispatch = await RunAsync(h);

        Assert.Empty(h.Client.CampaignCreates);
        Assert.Empty(h.Client.Calls);
        Assert.Equal("Failed", Assert.Single(dispatch.Batches).Status);
    }

    [Fact]
    public async Task OnlyACaseVariantExists_FailsInsteadOfCreatingAGenesysDuplicate()
    {
        using var h = await SeededAsync();
        h.Client.Campaigns.Add(new("a", StageName.ToUpperInvariant(), CurrentMonthList, "off"));
        var dispatch = await RunAsync(h);

        Assert.Empty(h.Client.CampaignCreates);
        Assert.Empty(h.Client.Calls);
        Assert.Contains("letter case", Assert.Single(dispatch.Batches).Error);
    }

    [Fact]
    public async Task CreationOff_AndNoCampaign_FailsWithoutUploading()
    {
        using var h = await SeededAsync(c => c.AllowCreate = false);
        var dispatch = await RunAsync(h);

        Assert.Empty(h.Client.CampaignCreates);
        Assert.Empty(h.Client.Calls);
        Assert.Contains("switched off", Assert.Single(dispatch.Batches).Error);
    }

    [Fact]
    public async Task IncompleteTemplate_FailsWithoutCallingGenesysToCreate()
    {
        using var h = await SeededAsync(c => c.QueueId = null);
        var dispatch = await RunAsync(h);

        Assert.Empty(h.Client.CampaignCreates);
        Assert.Empty(h.Client.Calls);
        Assert.Contains("QueueId", Assert.Single(dispatch.Batches).Error);
    }

    [Fact]
    public async Task FailedLookup_NeverCreates_AndNeverUploads()
    {
        using var h = await SeededAsync();
        h.Client.LookupScript.Enqueue(GenesysUploadOutcome.Unknown);
        var dispatch = await RunAsync(h);

        Assert.Empty(h.Client.CampaignCreates);
        Assert.Empty(h.Client.Calls);
        Assert.Equal("Failed", Assert.Single(dispatch.Batches).Status);
    }

    [Fact]
    public async Task UnconfirmedCreation_UploadsNothing_AndTheNextSendReusesTheCampaign()
    {
        using var h = await SeededAsync();
        h.Client.CreateScript.Enqueue(GenesysUploadOutcome.Unknown);
        h.Client.CreateUnknownStillCreates = true;           // Genesys made it, the answer was lost
        var first = await RunAsync(h, "k1");

        Assert.Empty(h.Client.Calls);
        Assert.Equal("Failed", Assert.Single(first.Batches).Status);
        Assert.Contains("check Genesys", Assert.Single(first.Batches).Error);

        var second = await RunAsync(h, "k2");               // the records were released, so they can be approved again
        Assert.Single(h.Client.CampaignCreates);              // still just the one create
        Assert.Equal("ReusedExisting", Assert.Single(second.Batches).CampaignAction);
        Assert.Equal("Uploaded", Assert.Single(second.Batches).Status);
    }

    [Fact]
    public async Task TwoSendsOfTheSameStageAtOnce_CreateTheGenesysCampaignOnce_AndTheSecondReusesIt()
    {
        using var h = await SeededAsync();
        h.Client.BeforeCreate = () => Task.Delay(300);                  // the first send is still inside "create" when the second one looks the campaign up
        var (_, first) = await h.PrepareAsync(h.All(new ReviewFilter(ReminderType: "CurrentMonth", Customer: "Customer 1")), "k1");
        var (_, second) = await h.PrepareAsync(h.All(new ReviewFilter(ReminderType: "CurrentMonth", Customer: "Customer 2")), "k2");
        long id1, id2;
        using (var scope = h.NewScope()) { id1 = (await scope.Dispatch.ConfirmAsync(h.Manager, first, CancellationToken.None)).Value!.DispatchId is var a ? h.Scheduler.Dispatches.Last() : 0; }
        using (var scope = h.NewScope()) { id2 = (await scope.Dispatch.ConfirmAsync(h.Manager, second, CancellationToken.None)).Value!.DispatchId is var b ? h.Scheduler.Dispatches.Last() : 0; }
        Assert.NotEqual(id1, id2);

        async Task Execute(long id) { using var scope = h.NewScope(); await scope.Dispatch.ExecuteAsync(id, CancellationToken.None); }
        await Task.WhenAll(Execute(id1), Execute(id2));

        Assert.Single(h.Client.CampaignCreates);                         // never two campaigns for one stage
        Assert.Equal(2, h.Client.Calls.Count);                           // both lists were uploaded
        using var context = h.CreateContext();
        var actions = await context.CollectionsGenesysBatches.AsNoTracking().Select(b => b.CampaignAction).OrderBy(x => x).ToListAsync();
        Assert.Equal(["Created", "ReusedExisting"], actions);
    }

    [Fact]
    public async Task CampaignHandlingOff_KeepsTheCurrentUploadFlow_AndStillRecordsTheStageName()
    {
        using var h = await SeededAsync(enabled: false);
        var dispatch = await RunAsync(h);

        Assert.Empty(h.Client.CampaignLookups);
        Assert.Empty(h.Client.CampaignCreates);
        var batch = Assert.Single(dispatch.Batches);
        Assert.Equal("Uploaded", batch.Status);
        Assert.Equal(StageName, batch.StageName);
        Assert.Null(batch.GenesysCampaignId);
    }

    [Fact]
    public async Task StageNameAndCampaignIdArePersisted()
    {
        using var h = await SeededAsync();
        await RunAsync(h);

        using var context = h.CreateContext();
        var stored = await context.CollectionsGenesysBatches.AsNoTracking().SingleAsync();
        Assert.Equal(StageName, stored.StageName);
        Assert.Equal(StageName, stored.GenesysCampaignName);
        Assert.Equal("campaign-1", stored.GenesysCampaignId);
    }

    [Fact]
    public void CommittedConfiguration_KeepsCampaignHandlingAndCreationOff_WithNoIdsFilledIn()
    {
        var committed = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TigerCS.Api", "appsettings.json"));
        using var json = System.Text.Json.JsonDocument.Parse(committed);
        var campaign = json.RootElement.GetProperty("Collections").GetProperty("GenesysOutbound").GetProperty("Campaign");
        Assert.False(campaign.GetProperty("Enabled").GetBoolean());
        Assert.False(campaign.GetProperty("AllowCreate").GetBoolean());
        foreach (var key in new[] { "QueueId", "DialingMode", "CallerName", "CallerAddress", "ScriptId", "CallAnalysisResponseSetId" })
            Assert.Equal("", campaign.GetProperty(key).GetString());
    }

    [Fact]
    public async Task ACampaignIsNeverSwitchedOn_ByTheSend()
    {
        using var h = await SeededAsync();
        h.Client.Campaigns.Add(new("existing-1", StageName, CurrentMonthList, "off"));
        await RunAsync(h);
        Assert.All(h.Client.Campaigns, c => Assert.Equal("off", c.Status));      // reuse and creation both leave it off; there is no activation call on the client at all
        Assert.DoesNotContain(typeof(IGenesysOutboundClient).GetMethods(), m => m.Name.Contains("Activate", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Start", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DefaultConfigurationDoesNotTouchCampaigns()
    {
        var options = new GenesysOutboundOptions();
        Assert.False(options.Campaign.Enabled);
        Assert.False(options.Campaign.AllowCreate);
        Assert.Equal(["QueueId", "DialingMode", "CallerName", "CallerAddress"], options.Campaign.MissingForCreate());
    }
}
