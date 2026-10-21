using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Tests.Collections.Crm;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// EF Core's default (non-retrying) SQL Server strategy turns a transient SQL error on a WRITE (ExecuteUpdate, SaveChanges, transactions) into
/// "InvalidOperationException: An exception has been raised that is likely due to a transient failure". That must never surface as the refresh's error: idempotent writes are retried
/// in a bounded way, informational ones cannot fail the refresh, and the compare-and-set start is reported clearly instead of being retried blindly.
/// </summary>
public sealed class ReviewTransientErrorTests
{
    private static ReviewHarness Seeded(int rows = 5)
    {
        var h = new ReviewHarness();
        for (var n = 1; n <= rows; n++) h.Source.Items.Add(ReviewHarness.Row(n, 100m + n));
        return h;
    }

    private static async Task<ReviewRunDto> RunAsync(ReviewHarness h)
    {
        using var scope = h.NewScope();
        var started = await scope.Refresh.StartAsync(h.Manager, null, CancellationToken.None);
        await scope.Refresh.RunAsync(started.Value!.RunId, CancellationToken.None);
        return (await scope.Query.GetRunAsync(h.Manager, started.Value.RunId, CancellationToken.None)).Value!;
    }

    [Fact]
    public async Task ATransientErrorOnAProgressWrite_WrappedByEf_IsRetried_AndTheRefreshCompletes()
    {
        using var h = Seeded();
        h.Faults.FailProgress = 1;
        var run = await RunAsync(h);
        Assert.Equal("Completed", run.Status);
        Assert.True(h.Faults.ProgressAttempts > 1);
    }

    [Fact]
    public async Task ProgressWritesThatKeepFailing_NeverFailTheRefresh_ItIsInformationalOnly()
    {
        using var h = Seeded();
        h.Faults.ProgressPersistent = true;
        var run = await RunAsync(h);
        Assert.Equal("Completed", run.Status);
        Assert.True(run.RecordCount > 0);
        Assert.DoesNotContain("InvalidOperationException", run.Error ?? "");
    }

    [Fact]
    public async Task AFailureToStart_IsFailedClearly_NotRetriedBlindly_AndNeverLeftQueued()
    {
        using var h = Seeded();
        h.Faults.FailStart = 1;
        var run = await RunAsync(h);
        Assert.Equal("Failed", run.Status);
        Assert.Contains("Could not start the review refresh", run.Error);
        Assert.DoesNotContain("likely due to a transient failure", run.Error);
        Assert.Equal(1, h.Faults.StartAttempts);                                  // compare-and-set: one attempt only
    }

    [Fact]
    public async Task ATransientReadOfTheRun_IsRetried()
    {
        using var h = Seeded();
        await RunAsync(h);
        h.Faults.FailReads = 1;
        using var scope = h.NewScope();
        var current = await scope.Store.GetCurrentRunAsync(CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(0, h.Faults.FailReads);
    }

    [Fact]
    public async Task AnUnexpectedFailure_NamesThePhaseItHappenedIn()
    {
        using var h = Seeded();
        h.Source.Failure = new InvalidOperationException("boom");
        var run = await RunAsync(h);
        Assert.Equal("Failed", run.Status);
        Assert.StartsWith("Failed while Reading the financial source: InvalidOperationException: boom", run.Error);
    }

    [Fact]
    public async Task ARefreshThatCannotRecordItsFailure_DoesNotCrashTheJob()
    {
        using var h = Seeded();
        h.Source.Failure = new InvalidOperationException("boom");
        h.Faults.FailBeforeSave = 0;
        h.RetryPolicy = new ReviewWriteRetryPolicy { MaxAttempts = 1, Delay = _ => TimeSpan.Zero, ExtraTransient = e => e is TransientTestException };
        // Nothing to assert beyond "no exception": the failure write is retried (here once) and, if it still fails, logged and left to the stuck-run timeout.
        var run = await RunAsync(h);
        Assert.Equal("Failed", run.Status);
    }

    [Theory]
    [InlineData(10060)] [InlineData(-1)] [InlineData(10928)] [InlineData(10929)] [InlineData(10936)] [InlineData(41301)] [InlineData(40613)]
    public void TheSqlNumbersEfTreatsAsTransient_AreRetried_EvenWhenEfWrapsThemInInvalidOperationException(int number)
    {
        var wrapped = new InvalidOperationException(CommandFaults.EfMessage, SqlExceptionFactory.Create(number));
        Assert.True(new ReviewWriteRetryPolicy().IsTransient(wrapped));
        Assert.Equal(number, ReviewWriteRetryPolicy.NumberOf(wrapped));
    }

    [Fact]
    public void AWrappedConstraintViolation_IsNeverTransient()
    {
        Assert.False(new ReviewWriteRetryPolicy().IsTransient(new InvalidOperationException(CommandFaults.EfMessage, SqlExceptionFactory.Create(2627))));
        Assert.False(new ReviewWriteRetryPolicy().IsTransient(new InvalidOperationException("The configured execution strategy does not support user-initiated transactions.")));
    }
}
