namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// FR-CH-02 — a fixed, extensible enum so later channels are additive, not
/// a rework. MVP scope is Phone only (FR-CH-01); the other members are
/// Phase 2/3 (Solution-Analysis.md §2.2) and are not reachable through any
/// endpoint in this increment.
/// </summary>
public enum Channel : byte
{
    Phone = 1,
    AppOrWebsite = 2,
    WhatsAppOrLiveChat = 3,
    SocialMediaDirectMessage = 4,
    FaceToFaceKiosk = 5
}
