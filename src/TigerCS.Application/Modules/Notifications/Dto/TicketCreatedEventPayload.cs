using System.Text.Json;
using System.Text.Json.Serialization;

namespace TigerCS.Application.Modules.Notifications.Dto;

/// <summary>
/// The <c>TicketCreated</c> Outbox payload (MVP-API-Contracts.md §3.1/§2.6).
///
/// <para>
/// <b>Identifiers only, deliberately.</b> No requester name, no contact
/// channel, no request summary. An Outbox row is long-lived and
/// operator-visible, and Security-Architecture.md §11 requires customer
/// contact details be masked or omitted from operational output — so the
/// handler re-reads whatever it needs from the database at dispatch time
/// instead. The cost is one extra read per dispatch; the benefit is that no
/// customer data is duplicated into an infrastructure table at all.
/// </para>
///
/// <para>
/// <b><see cref="EventVersion"/> is part of ADR-0014's idempotency key</b>
/// (<c>TicketId + EventType + EventVersion</c>) and is carried in the payload
/// too so a consumer reading an old row can tell which shape it is looking
/// at.
/// </para>
/// </summary>
public sealed record TicketCreatedEventPayload(
    [property: JsonPropertyName("ticketId")] long TicketId,
    [property: JsonPropertyName("eventVersion")] int EventVersion)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>Returns <c>null</c> for malformed or empty JSON rather than throwing — the dispatcher turns that into a permanent failure, since no retry will make an unparseable payload parse.</summary>
    public static TicketCreatedEventPayload? FromJson(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<TicketCreatedEventPayload>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
