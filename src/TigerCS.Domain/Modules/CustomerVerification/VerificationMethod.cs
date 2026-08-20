namespace TigerCS.Domain.Modules.CustomerVerification;

/// <summary>
/// How a requester's identity/match was confirmed — deliberately
/// channel-neutral (see <see cref="VerificationSession"/>'s remarks). This
/// pilot phase's only caller uses <see cref="ManualAgentConfirmation"/>
/// (an agent's verbal read-back over the phone, FR-VER-03), but the
/// domain, API, persistence, and audit trail all support every value below
/// without change — a future channel supplies its own value, not a new
/// concept.
/// </summary>
public enum VerificationMethod
{
    /// <summary>An agent confirmed the match directly with the caller (this pilot's only phone-channel method, FR-VER-03's verbal read-back).</summary>
    ManualAgentConfirmation = 1,

    /// <summary>The requester was already an authenticated digital user (e.g. a logged-in portal/app session) at the point of confirmation.</summary>
    AuthenticatedDigitalUser = 2,

    /// <summary>Confirmed via a one-time passcode sent to a CRM-recorded contact channel.</summary>
    Otp = 3,

    /// <summary>Confirmed in person against a physical identity/ownership document (e.g. a kiosk or front-desk channel).</summary>
    FaceToFaceDocumentCheck = 4,

    /// <summary>Any confirmation method not covered by the named values above.</summary>
    Other = 5
}
