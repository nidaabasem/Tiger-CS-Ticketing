using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.18 / MVP-ERD.md §2.18 — immutable once written; no update/delete path.</summary>
public class TicketNoteConfiguration : IEntityTypeConfiguration<TicketNote>
{
    public void Configure(EntityTypeBuilder<TicketNote> builder)
    {
        builder.ToTable("TicketNotes");

        builder.HasKey(n => n.TicketNoteId);
        builder.Property(n => n.TicketNoteId).ValueGeneratedOnAdd();

        builder.Property(n => n.NoteText).HasMaxLength(2000).IsRequired();
        builder.Property(n => n.CreatedAtUtc).IsRequired();

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(n => n.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(n => n.AuthorEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
