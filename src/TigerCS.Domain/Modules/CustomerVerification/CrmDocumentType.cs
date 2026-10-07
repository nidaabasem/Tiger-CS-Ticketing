namespace TigerCS.Domain.Modules.CustomerVerification;

/// <summary>
/// The four customer document copies the chatbot may ask CRM to send. Values
/// are stored (<c>CrmDocumentDeliveryRequests.DocumentType</c>) — never
/// renumber.
/// </summary>
public enum CrmDocumentType : byte
{
    Contract = 1,
    ReservationForm = 2,
    UnitLayout = 3,
    RegistrationReceipt = 4
}

/// <summary>How a document copy reaches the customer. Only <see cref="Email"/> has an integration; the others are recognised so the API can say precisely what is missing.</summary>
public enum DocumentDeliveryChannel : byte
{
    Email = 1,
    WhatsApp = 2,
    Sms = 3
}

public enum DocumentDeliveryStatus : byte
{
    /// <summary>A send has been claimed and is running (or its process died — see <see cref="CrmDocumentDeliveryRequest.IsStale"/>).</summary>
    InProgress = 1,

    /// <summary>The delivery channel accepted the message. Terminal: a replay answers Sent and sends nothing.</summary>
    Sent = 2,

    /// <summary>The attempt failed. A retry with the same key may try again.</summary>
    Failed = 3
}
