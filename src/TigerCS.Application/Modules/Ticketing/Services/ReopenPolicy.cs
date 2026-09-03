using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// ISSUE-011's approved decision (Executive-Decisions.md row 17): a
/// Resolved/Closed ticket may be reopened within a fixed window measured
/// from its current resolution's <c>ResolvedAtUtc</c> — 7 days by default,
/// configurable (<c>Ticketing:ReopenWindowDays</c>, bound at DI
/// registration), never hard-coded at call sites. Beyond the window a new
/// linked ticket is created instead (BR-020) — the window check therefore
/// yields a distinct outcome, never a generic "not eligible".
/// </summary>
/// <param name="WindowDays">How many days after the current resolution a reopen stays allowed.</param>
public sealed record ReopenPolicy(int WindowDays)
{
    public const int DefaultWindowDays = 7;

    public static readonly ReopenPolicy Default = new(DefaultWindowDays);

    public TimeSpan Window => TimeSpan.FromDays(WindowDays);

    /// <summary>
    /// The single lifecycle-eligibility rule every Reopen surface shares —
    /// the authoritative check in <c>TicketLifecycleAppService.ReopenAsync</c>
    /// and the display-eligibility flags on detail/history DTOs both call
    /// this, so the button and the action can never disagree on the rule.
    /// Permission (TicketRoleSets.Reopen) is deliberately not part of it.
    /// </summary>
    public bool IsWithinWindow(DateTime resolvedAtUtc, DateTime nowUtc) => resolvedAtUtc + Window >= nowUtc;

    public bool IsReopenEligible(TicketStatus status, DateTime? resolvedAtUtc, DateTime nowUtc) =>
        status is TicketStatus.Resolved or TicketStatus.Closed
        && resolvedAtUtc is { } resolvedAt
        && IsWithinWindow(resolvedAt, nowUtc);
}
