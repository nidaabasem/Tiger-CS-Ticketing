using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/channels</c> endpoint — the configured channel directory the New Ticket wizard's Channel picker reads.</summary>
public sealed class ChannelsApiClient(HttpClient httpClient, ILogger<ChannelsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    /// <summary>The active channels, ordered by display order then name — what a new-ticket Channel picker may offer.</summary>
    public Task<ApiResult<IReadOnlyList<ChannelDto>>> GetChannelsAsync(CancellationToken cancellationToken) =>
        GetChannelsAsync(activeOnly: true, cancellationToken);

    /// <summary><paramref name="activeOnly"/> false includes deactivated channels — needed only to put a NAME on a channel a historical record references; a picker never offers those.</summary>
    public Task<ApiResult<IReadOnlyList<ChannelDto>>> GetChannelsAsync(bool activeOnly, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<ChannelDto>>($"api/channels?activeOnly={(activeOnly ? "true" : "false")}", cancellationToken);
}
