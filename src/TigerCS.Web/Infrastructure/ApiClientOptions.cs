using System.ComponentModel.DataAnnotations;

namespace TigerCS.Web.Infrastructure;

/// <summary>
/// How this application reaches TigerCS.Api. Bound from the <c>Api</c>
/// configuration section.
///
/// <para>
/// There is deliberately no credential of any kind here. Every call this
/// application makes to the API is authenticated with the access token of the
/// signed-in user, obtained from <c>POST /api/auth/login</c> and carried in
/// that user's own encrypted authentication cookie - never with a service
/// account, a shared key, or anything else that would need to be committed or
/// deployed as a secret.
/// </para>
/// </summary>
public sealed class ApiClientOptions
{
    public const string SectionName = "Api";

    /// <summary>
    /// Absolute base address of TigerCS.Api, e.g. <c>https://localhost:7043</c>.
    /// Required outside the test host, which supplies its own transport.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Per-request timeout. Kept short: every call sits in front of a waiting agent.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether the ticket queue resolves each visible row's SLA state through
    /// <c>GET /api/tickets/{id}/sla</c>.
    ///
    /// <para>
    /// This exists because <c>TicketSummaryDto</c> carries no SLA field, while
    /// <c>MVP-API-Contracts.md</c> §3.2 specifies that it should ("just
    /// <c>SlaState</c> and the current due timestamp for at-a-glance display")
    /// and backlog S-22 requires the queue to show SLA badges. Resolving per
    /// row is the only way to satisfy both without changing a backend DTO, and
    /// it costs one extra API call per row on the current page. It is bounded
    /// (<see cref="SlaResolutionMaxDegreeOfParallelism"/>, and the page only),
    /// briefly cached, and switchable off here for a deployment that would
    /// rather have a faster queue than SLA badges.
    /// </para>
    /// </summary>
    public bool ResolveQueueSlaPerRow { get; set; } = true;

    /// <summary>Upper bound on concurrent per-row SLA calls. Clamped to 1-16.</summary>
    [Range(1, 16)]
    public int SlaResolutionMaxDegreeOfParallelism { get; set; } = 6;

    /// <summary>
    /// How long a resolved department roster (<c>GET /api/departments/{id}/users</c>)
    /// stays cached for owner-name lookup. Names change rarely; a stale name for
    /// this long is a better trade than a roster fetch per queue render.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan DirectoryCacheDuration { get; set; } = TimeSpan.FromMinutes(5);
}
