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

/// <summary>Which door asked for a document copy. Stored explicitly so a row never has to be guessed from which columns are null.</summary>
public enum DocumentDeliveryOrigin : byte
{
    /// <summary>The Genesys chatbot, proven by a real OTP-verified <c>VerificationSession</c> (the default for every row that existed before agent sends).</summary>
    GenesysVerifiedSession = 1,

    /// <summary>A signed-in agent on Customer Details → Documents; no verification session exists.</summary>
    AgentDesk = 2
}

public enum DocumentDeliveryStatus : byte
{
    /// <summary>A send has been claimed and is running (or its process died — see <see cref="CrmDocumentDeliveryRequest.IsStale"/>).</summary>
    InProgress = 1,

    /// <summary>The delivery channel accepted the message. Terminal: a replay answers Sent and sends nothing.</summary>
    Sent = 2,

    /// <summary>The attempt failed. A retry with the same key may try again.</summary>
    Failed = 3,

    /// <summary>
    /// Written (and saved) <b>before</b> the document is handed to the delivery channel. A row left here means the message may
    /// already have been accepted by the provider while the outcome was never recorded (database timeout, crash, cancelled
    /// request). Unlike <see cref="InProgress"/> it is never retaken automatically: a retry answers "unconfirmed" and sends nothing.
    /// Stored as a plain byte — no schema change.
    /// </summary>
    Dispatching = 4
}
