namespace TigerCS.Application.Modules.CustomerVerification.Abstractions;

/// <summary>
/// A row changed between being read and being saved (an EF concurrency token
/// mismatch). The OTP flow relies on it for single use and for an honest
/// attempt budget: the loser re-reads and re-evaluates against the winner's
/// state.
/// </summary>
public sealed class ConcurrentWriteException(Exception innerException)
    : Exception("A concurrent write changed the row first.", innerException);
