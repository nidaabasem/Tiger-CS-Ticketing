namespace TigerCS.Domain.Modules.Collections;

public enum CollectionsDueClass
{
    /// <summary>Due on or after the first day of the NEXT month: part of neither Due nor Overdue (never listed, totalled or exported).</summary>
    NotInScope = 0,
    /// <summary>Due before the first day of the current month.</summary>
    Overdue = 1,
    /// <summary>Due inside the current month, whichever day (past, today or a coming day of the month).</summary>
    Due = 2
}

/// <summary>
/// THE Due / Overdue rule of Receivables and Campaigns, against the CURRENT calendar month in Asia/Dubai (the caller passes today's Dubai date):
/// <code>Overdue: DueDate &lt; monthStart
/// Due:     DueDate &gt;= monthStart AND DueDate &lt; nextMonthStart
/// (DueDate &gt;= nextMonthStart is excluded)</code>
/// It is calendar based (December rolls into January through <see cref="DateOnly.AddMonths"/>), never day based, and it is the single definition: the SQL read
/// procedures (<c>usp_CollectionsRcv_GetUnits</c>) apply the same two boundaries and the application never re-derives them. Amounts are always the REMAINING
/// amount of an unpaid or partly paid instalment.
/// </summary>
public static class CollectionsDueMonth
{
    public static DateOnly MonthStart(DateOnly today) => new(today.Year, today.Month, 1);

    public static DateOnly NextMonthStart(DateOnly today) => MonthStart(today).AddMonths(1);

    /// <summary>Last day of the current month: the widest due date Receivables / Campaigns ever read.</summary>
    public static DateOnly MonthEnd(DateOnly today) => NextMonthStart(today).AddDays(-1);

    public static CollectionsDueClass Classify(DateOnly dueDate, DateOnly today) =>
        dueDate < MonthStart(today) ? CollectionsDueClass.Overdue
        : dueDate < NextMonthStart(today) ? CollectionsDueClass.Due
        : CollectionsDueClass.NotInScope;

    public static bool IsOverdue(DateOnly dueDate, DateOnly today) => dueDate < MonthStart(today);

    public static bool IsDue(DateOnly dueDate, DateOnly today) => dueDate >= MonthStart(today) && dueDate < NextMonthStart(today);

    /// <summary>Due or Overdue: due before the first day of the next month.</summary>
    public static bool IsInScope(DateOnly dueDate, DateOnly today) => dueDate < NextMonthStart(today);

    /// <summary>The upper end (inclusive) a due-date window is clamped to: nothing from the next month onwards is read.</summary>
    public static DateOnly ClampTo(DateOnly to, DateOnly today)
    {
        var end = MonthEnd(today);
        return to > end ? end : to;
    }
}
