namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// MVP-ERD.md §2.18 / MVP-Data-Dictionary.md §2.18 — an internal note.
/// Immutable once written; no update/delete path exists at any privilege
/// level (a correction is posted as a new note). Customer-facing comments
/// are out of scope for this increment.
/// </summary>
public class TicketNote
{
    public long TicketNoteId { get; private set; }
    public long TicketId { get; private set; }
    public string NoteText { get; private set; } = string.Empty;
    public Guid AuthorEmployeeId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private TicketNote() { }

    public TicketNote(long ticketId, string noteText, Guid authorEmployeeId, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(noteText))
        {
            throw new ArgumentException("NoteText is required.", nameof(noteText));
        }

        if (authorEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("AuthorEmployeeId is required.", nameof(authorEmployeeId));
        }

        TicketId = ticketId;
        NoteText = noteText;
        AuthorEmployeeId = authorEmployeeId;
        CreatedAtUtc = createdAtUtc;
    }
}
