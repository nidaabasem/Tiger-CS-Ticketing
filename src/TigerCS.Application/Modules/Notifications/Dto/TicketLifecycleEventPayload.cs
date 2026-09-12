using System.Text.Json;
using System.Text.Json.Serialization;

namespace TigerCS.Application.Modules.Notifications.Dto;

/// <summary>
/// Payload of the <c>TicketResolved</c> / <c>TicketClosed</c> /
/// <c>TicketReopened</c> Outbox events. Identifiers only — the handler
/// re-reads the ticket, so nothing customer-facing is ever serialised into
/// <c>OutboxMessages.Payload</c>.
/// </summary>
/// <param name="TicketId">The ticket the event concerns.</param>
/// <param name="EventVersion">ADR-0014's event version component.</param>
/// <param name="ReopenCount">
/// The ticket's resolution cycle at the time of the event; mirrors the
/// <c>cycle</c> component of the idempotency key so a payload is
/// self-describing when read from the dead-letter queue.
/// </param>
public sealed record TicketLifecycleEventPayload(
    [property: JsonPropertyName("ticketId")] long TicketId,
    [property: JsonPropertyName("eventVersion")] int EventVersion,
    [property: JsonPropertyName("reopenCount")] int ReopenCount)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static TicketLifecycleEventPayload? FromJson(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<TicketLifecycleEventPayload>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
