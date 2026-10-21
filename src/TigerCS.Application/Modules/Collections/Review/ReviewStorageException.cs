namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// Writing review data to the database failed. <see cref="Transient"/> says the failure was a connection loss / timeout that still persisted after the bounded retries;
/// the message is safe to show (no connection string, no data).
/// </summary>
public sealed class ReviewStorageException(string message, bool transient, int? sqlNumber = null, Exception? inner = null) : Exception(message, inner)
{
    public bool Transient { get; } = transient;
    public int? SqlNumber { get; } = sqlNumber;
}
