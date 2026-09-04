using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>Workflow/Automation phase 2 — structured pending periods; append-plus-resume, never deleted.</summary>
public class TicketPendingRecordConfiguration : IEntityTypeConfiguration<TicketPendingRecord>
{
    public void Configure(EntityTypeBuilder<TicketPendingRecord> builder)
    {
        builder.ToTable("TicketPendingRecords");

        builder.HasKey(p => p.TicketPendingRecordId);
        builder.Property(p => p.TicketPendingRecordId).ValueGeneratedOnAdd();

        builder.Property(p => p.Kind).HasConversion<byte>().IsRequired();
        builder.Property(p => p.Reason).HasMaxLength(500).IsRequired();
        builder.Property(p => p.PreviousStatus).HasConversion<byte>().IsRequired();

        // At most one open pending period per ticket — the status machine
        // allows only one Pending state at a time, and the filtered unique
        // index makes that invariant a database guarantee, not a code hope.
        builder.HasIndex(p => p.TicketId)
            .HasFilter("[ResumedAtUtc] IS NULL")
            .IsUnique()
            .HasDatabaseName("UX_TicketPendingRecords_OpenPerTicket");

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(p => p.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(p => p.StartedByEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(p => p.ResumedByEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
