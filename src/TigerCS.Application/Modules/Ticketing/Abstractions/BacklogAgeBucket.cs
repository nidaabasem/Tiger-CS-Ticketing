namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>
/// The Open Backlog Ageing buckets (Dashboard Phase 1). Age is measured from
/// the ticket's own <c>CreatedAtUtc</c> to the evaluation instant, and only
/// currently active tickets are ever bucketed — Resolved/Closed tickets
/// have no backlog age.
///
/// <para>
/// <b>Boundaries are half-open and explicit</b> (see
/// <see cref="BacklogAgeBoundaries"/>): a ticket exactly 24 hours old is
/// <see cref="OneToThreeDays"/>, exactly 72 hours old is
/// <see cref="ThreeToSevenDays"/>, exactly 168 hours old is
/// <see cref="OverSevenDays"/>. The same enum is the ticket queue's
/// <c>backlogAge</c> filter, so a dashboard bucket and its drill-down list
/// always agree.
/// </para>
/// </summary>
public enum BacklogAgeBucket
{
    /// <summary>Age &lt; 24 hours.</summary>
    Under24Hours = 1,

    /// <summary>24 hours ≤ age &lt; 72 hours.</summary>
    OneToThreeDays = 2,

    /// <summary>72 hours ≤ age &lt; 168 hours.</summary>
    ThreeToSevenDays = 3,

    /// <summary>Age ≥ 168 hours.</summary>
    OverSevenDays = 4
}

/// <summary>
/// The exact thresholds behind <see cref="BacklogAgeBucket"/>, expressed as
/// the CreatedAtUtc cut-offs for one evaluation instant so a repository can
/// apply them as plain column comparisons (SQL-side, index-friendly):
/// a ticket is in a bucket when <c>Lower &lt; CreatedAtUtc ≤ Upper</c>
/// (<c>Upper</c> null = no upper bound, <c>Lower</c> null = no lower bound).
/// </summary>
public static class BacklogAgeBoundaries
{
    public static readonly TimeSpan OneDay = TimeSpan.FromHours(24);
    public static readonly TimeSpan ThreeDays = TimeSpan.FromHours(72);
    public static readonly TimeSpan SevenDays = TimeSpan.FromHours(168);

    /// <summary>The (exclusive lower, inclusive upper) CreatedAtUtc window of one bucket at <paramref name="nowUtc"/>.</summary>
    public static (DateTime? CreatedAfterUtc, DateTime? CreatedAtOrBeforeUtc) CreatedAtWindow(BacklogAgeBucket bucket, DateTime nowUtc) =>
        bucket switch
        {
            // age < 24h  ⇔  CreatedAt > now - 24h
            BacklogAgeBucket.Under24Hours => (nowUtc - OneDay, null),
            // 24h ≤ age < 72h  ⇔  now - 72h < CreatedAt ≤ now - 24h
            BacklogAgeBucket.OneToThreeDays => (nowUtc - ThreeDays, nowUtc - OneDay),
            // 72h ≤ age < 168h  ⇔  now - 168h < CreatedAt ≤ now - 72h
            BacklogAgeBucket.ThreeToSevenDays => (nowUtc - SevenDays, nowUtc - ThreeDays),
            // age ≥ 168h  ⇔  CreatedAt ≤ now - 168h
            BacklogAgeBucket.OverSevenDays => (null, nowUtc - SevenDays),
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, "Unknown backlog age bucket.")
        };

    /// <summary>The bucket a ticket created at <paramref name="createdAtUtc"/> falls in at <paramref name="nowUtc"/> — the in-memory twin of <see cref="CreatedAtWindow"/>, for fakes and tests.</summary>
    public static BacklogAgeBucket BucketOf(DateTime createdAtUtc, DateTime nowUtc)
    {
        var age = nowUtc - createdAtUtc;
        return age < OneDay ? BacklogAgeBucket.Under24Hours
            : age < ThreeDays ? BacklogAgeBucket.OneToThreeDays
            : age < SevenDays ? BacklogAgeBucket.ThreeToSevenDays
            : BacklogAgeBucket.OverSevenDays;
    }
}
