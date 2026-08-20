namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>
/// Thrown when a lost optimistic-concurrency race is detected on
/// <c>Tickets.RowVersion</c> — another request modified the same ticket
/// (assignment, transfer, status change, resolve, close, or reconciliation)
/// between this request's read and its write. The caller should surface a
/// `409`/`412` and let the client retry against the ticket's current state
/// (its own `If-Match` header pattern, mirroring MVP-API-Contracts.md's
/// other mutating ticket endpoints).
/// </summary>
public sealed class TicketConcurrentlyModifiedException(Exception innerException)
    : Exception("A concurrent request already modified this ticket.", innerException);
