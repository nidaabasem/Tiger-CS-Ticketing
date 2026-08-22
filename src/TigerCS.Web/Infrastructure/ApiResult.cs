namespace TigerCS.Web.Infrastructure;

/// <summary>
/// The outcome of one API call: a value, or the problem document that
/// explains why there isn't one.
///
/// <para>
/// Every typed client returns this rather than throwing on a non-success
/// status. A 403, 409, 410, 422 or 423 from this API is an expected,
/// meaningful answer that a page must render differently - not an exceptional
/// condition. Exceptions are reserved for transport failures, which
/// <see cref="ApiExceptionHandler"/> converts into a 502-shaped result at the
/// boundary so no page ever has to catch one.
/// </para>
/// </summary>
public readonly struct ApiResult<T>
{
    private ApiResult(T? value, ApiProblem? problem)
    {
        Value = value;
        Problem = problem;
    }

    public T? Value { get; }

    public ApiProblem? Problem { get; }

    public bool IsSuccess => Problem is null;

    /// <summary>The value of a successful result. Throws when the result is a failure - check <see cref="IsSuccess"/> first.</summary>
    public T Required => IsSuccess && Value is not null
        ? Value
        : throw new InvalidOperationException($"No value: {Problem?.Title ?? "unsuccessful API result"}.");

    public static ApiResult<T> Success(T value) => new(value, null);

    public static ApiResult<T> Failure(ApiProblem problem) => new(default, problem);
}

/// <summary>The same outcome for a call that returns no body.</summary>
public readonly struct ApiResult
{
    private ApiResult(ApiProblem? problem) => Problem = problem;

    public ApiProblem? Problem { get; }

    public bool IsSuccess => Problem is null;

    public static ApiResult Success() => new(null);

    public static ApiResult Failure(ApiProblem problem) => new(problem);
}
