using TigerCS.Application.Modules.Collections;
using TigerCS.Infrastructure.BackgroundJobs;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>The schedule note shown on the pages reads the same two settings the job registration reads, and the shipped configuration keeps background jobs off.</summary>
public sealed class SnapshotScheduleTests
{
    [Theory]
    [InlineData(false, true, false, "BackgroundJobs:Enabled is false")]
    [InlineData(true, false, false, "RefreshEnabled is false")]
    [InlineData(true, true, true, "cron \"0 */2 * * *\" (Asia/Dubai)")]
    public void TheNoteFollowsTheRealSettings(bool jobsEnabled, bool refreshEnabled, bool active, string expectedText)
    {
        var schedule = new SnapshotSchedule(new BackgroundJobOptions { Enabled = jobsEnabled }, new ReceivablesSnapshotOptions { RefreshEnabled = refreshEnabled });
        Assert.Equal(active, schedule.ScheduledRefreshActive);
        Assert.Contains(expectedText, schedule.Describe());
    }

    [Fact]
    public void TheNoteMentionsTheRunAtEveryStartup_WhenThatIsOn()
    {
        var on = new SnapshotSchedule(new BackgroundJobOptions { Enabled = true }, new ReceivablesSnapshotOptions { RefreshOnStartup = true }).Describe();
        var off = new SnapshotSchedule(new BackgroundJobOptions { Enabled = true }, new ReceivablesSnapshotOptions { RefreshOnStartup = false }).Describe();
        Assert.Contains("at every application start", on); Assert.DoesNotContain("at every application start", off);
    }

    [Fact]
    public void TheShippedConfiguration_KeepsEveryBackgroundJobOff_AndDoesNotEnableThemInUatOrDevelopment()
    {
        string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TigerCS.Api", file));
        using var shipped = System.Text.Json.JsonDocument.Parse(Read("appsettings.json"));
        Assert.False(shipped.RootElement.GetProperty("BackgroundJobs").GetProperty("Enabled").GetBoolean());
        foreach (var file in new[] { "appsettings.Development.json", "appsettings.UAT.json" })
        {
            using var doc = System.Text.Json.JsonDocument.Parse(Read(file));
            if (doc.RootElement.TryGetProperty("BackgroundJobs", out var section)) Assert.False(section.GetProperty("Enabled").GetBoolean(), file);
        }
    }
}
