namespace TigerCS.Domain.Modules.CustomerVerification;

/// <summary>MVP-Data-Dictionary.md §2.24 (VerificationSessions.Status).</summary>
public enum VerificationSessionStatus
{
    InProgress = 1,
    Confirmed = 2,
    Consumed = 3,
    Expired = 4,
    Abandoned = 5
}
