using Microsoft.AspNetCore.Http;

namespace TigerCS.Web.Infrastructure;

/// <summary>
/// The correlation id for the request currently being handled.
///
/// <para>
/// <b>What the API does with it today: nothing.</b> Every TigerCS.Api
/// application service mints its own correlation id
/// (<c>var correlationId = Guid.NewGuid()</c>) for the audit rows it writes,
/// and no middleware or controller reads one from an inbound header. The
/// header this application sends is therefore useful for correlating this
/// tier's own logs and for a reverse proxy's access log, and it is honestly
/// not end-to-end yet. It is sent anyway because the propagation costs
/// nothing and the alternative - waiting to send it until the API accepts it -
/// would need a backend change this increment must not make.
/// </para>
/// </summary>
public sealed class CorrelationIdAccessor(IHttpContextAccessor httpContextAccessor)
{
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>
    /// The inbound correlation id if a proxy supplied one, otherwise the
    /// request's own trace identifier. Never null, never empty.
    /// </summary>
    public string GetCorrelationId()
    {
        var context = httpContextAccessor.HttpContext;
        if (context is null)
        {
            return "no-http-context";
        }

        if (context.Request.Headers.TryGetValue(HeaderName, out var supplied))
        {
            var value = supplied.ToString();
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 128)
            {
                return value;
            }
        }

        return context.TraceIdentifier;
    }
}
