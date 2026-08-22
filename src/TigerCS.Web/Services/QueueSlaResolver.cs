using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Web.Infrastructure;

namespace TigerCS.Web.Services;

/// <summary>
/// Resolves the SLA state of the tickets on one queue page.
///
/// <para>
/// <b>Why this is not simply a field on the list response.</b>
/// <c>MVP-API-Contracts.md</c> §3.2 specifies that <c>TicketSummary</c> carries
/// "just <c>SlaState</c> and the current due timestamp for at-a-glance
/// display", and <c>MVP-Implementation-Backlog.md</c> S-22 requires the queue
/// to render SLA and escalation badges. The shipped <c>TicketSummaryDto</c>
/// carries neither, and adding a field to it would be a backend DTO change
/// this increment must not make. So the state is fetched per row from
/// <c>GET /api/tickets/{id}/sla</c>, which is a real, already-authorized
/// endpoint that answers exactly this question.
/// </para>
///
/// <para>
/// <b>The cost is real and is bounded three ways</b>: only the rows on the
/// current page are resolved, at most
/// <c>SlaResolutionMaxDegreeOfParallelism</c> calls are in flight at once, and
/// the whole behaviour can be switched off with
/// <c>Api:ResolveQueueSlaPerRow</c>. A row whose SLA cannot be read - because
/// it 403s, 404s, or the call fails - is rendered as "unknown" rather than as
/// "on track", because silently showing a breached ticket as healthy is the
/// one failure mode that actually matters here.
/// </para>
/// </summary>
public sealed class QueueSlaResolver(TicketSlaApiClient slaApiClient, IOptions<ApiClientOptions> options)
{
    private readonly ApiClientOptions _options = options.Value;

    /// <summary>Whether SLA badges are being resolved at all.</summary>
    public bool IsEnabled => _options.ResolveQueueSlaPerRow;

    /// <summary>
    /// SLA summaries for the given tickets, keyed by ticket id. A ticket whose
    /// summary could not be read is simply absent from the result.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, TicketSlaSummaryResponseDto>> ResolveAsync(
        IReadOnlyCollection<long> ticketIds, CancellationToken cancellationToken)
    {
        if (!IsEnabled || ticketIds.Count == 0)
        {
            return new Dictionary<long, TicketSlaSummaryResponseDto>();
        }

        var resolved = new ConcurrentDictionary<long, TicketSlaSummaryResponseDto>();
        using var gate = new SemaphoreSlim(Math.Clamp(_options.SlaResolutionMaxDegreeOfParallelism, 1, 16));

        var fetches = ticketIds.Distinct().Select(async ticketId =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var result = await slaApiClient.GetSlaAsync(ticketId, cancellationToken);
                if (result.IsSuccess && result.Value is not null)
                {
                    resolved[ticketId] = result.Value;
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(fetches);
        return resolved;
    }
}
