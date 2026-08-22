namespace TigerCS.Web.Infrastructure;

/// <summary>
/// One of two success shapes.
///
/// <para>
/// Exists for exactly one endpoint: <c>POST /api/tickets/provisional</c>
/// answers 201 with a ticket for Critical/High and 200 with an intake record
/// for Medium/Low (ISSUE-006's approved fallback). Both are successes and they
/// carry different types, so the caller has to be told which one arrived
/// rather than being handed a nullable of each.
/// </para>
/// </summary>
public readonly struct OneOf<TFirst, TSecond>
{
    private OneOf(TFirst? first, TSecond? second, bool isFirst)
    {
        First = first;
        Second = second;
        IsFirst = isFirst;
    }

    public TFirst? First { get; }

    public TSecond? Second { get; }

    public bool IsFirst { get; }

    public bool IsSecond => !IsFirst;

    public static OneOf<TFirst, TSecond> FromFirst(TFirst value) => new(value, default, true);

    public static OneOf<TFirst, TSecond> FromSecond(TSecond value) => new(default, value, false);
}
