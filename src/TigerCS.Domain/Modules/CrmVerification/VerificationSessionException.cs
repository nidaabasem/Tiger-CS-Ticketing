namespace TigerCS.Domain.Modules.CrmVerification;

/// <summary>Base type for violations of the VerificationSession state machine (MVP-ERD.md §2.24).</summary>
public abstract class VerificationSessionException(string message) : Exception(message);

public sealed class VerificationSessionNotInProgressException(Guid verificationSessionId, VerificationSessionStatus currentStatus)
    : VerificationSessionException(
        $"Verification session {verificationSessionId} is not in progress (current status: {currentStatus}).")
{
    public Guid VerificationSessionId { get; } = verificationSessionId;
    public VerificationSessionStatus CurrentStatus { get; } = currentStatus;
}

public sealed class VerificationSessionExpiredException(Guid verificationSessionId)
    : VerificationSessionException($"Verification session {verificationSessionId} has expired.")
{
    public Guid VerificationSessionId { get; } = verificationSessionId;
}

public sealed class VerificationSessionNotConfirmedException(Guid verificationSessionId)
    : VerificationSessionException($"Verification session {verificationSessionId} has not been confirmed.")
{
    public Guid VerificationSessionId { get; } = verificationSessionId;
}

public sealed class VerificationSessionAlreadyConsumedException(Guid verificationSessionId)
    : VerificationSessionException($"Verification session {verificationSessionId} has already been consumed by a ticket.")
{
    public Guid VerificationSessionId { get; } = verificationSessionId;
}
