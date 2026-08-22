using System.Net;
using System.Text.Json.Serialization;

namespace TigerCS.Web.Infrastructure;

/// <summary>
/// An RFC 7807 problem document as TigerCS.Api returns it, plus the status
/// code it arrived with.
///
/// <para>
/// The API answers every failure with a <c>ProblemDetails</c> or
/// <c>ValidationProblemDetails</c> body carrying a stable <c>type</c> URI
/// (for example <c>https://tigercs.internal/problems/ticket-closed</c>). The
/// UI branches on <see cref="Status"/> and, where it needs to distinguish two
/// failures that share a status code, on <see cref="ProblemType"/> - never on
/// <see cref="Title"/> or <see cref="Detail"/>, which are human-facing prose
/// and not a contract.
/// </para>
/// </summary>
public sealed class ApiProblem
{
    [JsonPropertyName("type")]
    public string? ProblemType { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("status")]
    public int? Status { get; init; }

    /// <summary>Field-level messages from a <c>ValidationProblemDetails</c>, keyed by field name.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }

    /// <summary>The status code the response actually carried, which is authoritative over <see cref="Status"/>.</summary>
    [JsonIgnore]
    public HttpStatusCode StatusCode { get; init; }

    /// <summary>The trailing segment of <see cref="ProblemType"/>, e.g. <c>ticket-closed</c>. Empty when absent.</summary>
    [JsonIgnore]
    public string ProblemCode =>
        string.IsNullOrWhiteSpace(ProblemType)
            ? string.Empty
            : ProblemType[(ProblemType.LastIndexOf('/') + 1)..];

    public static ApiProblem FromStatus(HttpStatusCode statusCode, string? title = null) =>
        new() { StatusCode = statusCode, Status = (int)statusCode, Title = title };

    /// <summary>
    /// The message shown to the user. Falls back to a description of the status
    /// code rather than exposing a raw exception or an empty banner.
    /// </summary>
    public string UserMessage() =>
        !string.IsNullOrWhiteSpace(Detail) ? Detail!
        : !string.IsNullOrWhiteSpace(Title) ? Title!
        : StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session has expired. Please sign in again.",
            HttpStatusCode.Forbidden => "You do not have permission to perform this action.",
            HttpStatusCode.NotFound => "That item was not found, or you do not have access to it.",
            HttpStatusCode.Conflict => "Someone else changed this first. Reload and try again.",
            HttpStatusCode.Gone => "That verification is no longer valid. Please verify again.",
            HttpStatusCode.UnprocessableEntity => "That change is not valid for this item's current state.",
            HttpStatusCode.Locked => "This account is locked. Contact your administrator.",
            HttpStatusCode.BadGateway => "Tiger CRM is currently unavailable.",
            _ => "The request could not be completed. Please try again."
        };
}
