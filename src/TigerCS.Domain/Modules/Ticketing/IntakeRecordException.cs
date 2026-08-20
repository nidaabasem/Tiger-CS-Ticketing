namespace TigerCS.Domain.Modules.Ticketing;

public abstract class IntakeRecordException(string message) : Exception(message);

public sealed class IntakeRecordAlreadyLinkedException(long intakeRecordId, long linkedTicketId)
    : IntakeRecordException($"IntakeRecord {intakeRecordId} is already linked to Ticket {linkedTicketId}.")
{
    public long IntakeRecordId { get; } = intakeRecordId;
    public long LinkedTicketId { get; } = linkedTicketId;
}

public sealed class IntakeRecordNotUnitRelatedException(long intakeRecordId)
    : IntakeRecordException($"IntakeRecord {intakeRecordId} is not unit-related and cannot be promoted to a ticket.")
{
    public long IntakeRecordId { get; } = intakeRecordId;
}
