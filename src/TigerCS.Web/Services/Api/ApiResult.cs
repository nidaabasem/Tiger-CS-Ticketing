namespace TigerCS.Web.Services.Api;

/// <summary>
/// How a TigerCS.Api call landed, collapsed from its HTTP status code.
/// Every Web page branches on this instead of on raw status codes, so the
/// mapping to a status/empty/error UI state lives in one place.
/// </summary>
public enum ApiOutcome
{
    Success,
    ValidationError,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Locked,
    UnprocessableEntity,
    BadGateway,

    /// <summary>503 — the Api, or a source behind it, is switched off or unavailable. <see cref="ApiResult{T}.ProblemType"/> says which.</summary>
    ServiceUnavailable,

    /// <summary>The Api could not be reached at all (network error, timeout, DNS) — distinct from a Bad Gateway the Api itself reported.</summary>
    Unreachable,

    Unknown
}

/// <summary>The result of a call that returns data on success.</summary>
public sealed record ApiResult<T>(ApiOutcome Outcome, T? Value = default, string? Detail = null, string? ProblemType = null)
{
    public bool IsSuccess => Outcome == ApiOutcome.Success;

    public static ApiResult<T> Success(T value) => new(ApiOutcome.Success, value);

    public static ApiResult<T> Failure(ApiOutcome outcome, string? detail = null, string? problemType = null) => new(outcome, default, detail, problemType);
}

/// <summary>The result of a call with no response body on success (e.g. logout).</summary>
public sealed record ApiResult(ApiOutcome Outcome, string? Detail = null, string? ProblemType = null)
{
    public bool IsSuccess => Outcome == ApiOutcome.Success;

    public static ApiResult Success() => new(ApiOutcome.Success);

    public static ApiResult Failure(ApiOutcome outcome, string? detail = null, string? problemType = null) => new(outcome, detail, problemType);
}
