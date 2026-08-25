namespace TigerCS.Domain.Modules.Ticketing;

public abstract class IntakeRecordException(string message) : Exception(message);

public sealed class IntakeRecordAlreadyLinkedException(long intakeRecordId, long linkedTicketId)
    : IntakeRecordException($"IntakeRecord {intakeRecordId} is already linked to Ticket {linkedTicketId}.")
{
    public long IntakeRecordId { get; } = intakeRecordId;
    public long LinkedTicketId { get; } = linkedTicketId;
}
