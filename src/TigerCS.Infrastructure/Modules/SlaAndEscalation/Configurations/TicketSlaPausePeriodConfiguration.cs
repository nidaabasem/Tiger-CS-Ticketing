using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Configurations;

/// <summary>
/// <c>TicketSlaPausePeriods</c> (ISSUE-018) — Resolution-clock pause history;
/// rows are never deleted.
/// </summary>
public class TicketSlaPausePeriodConfiguration : IEntityTypeConfiguration<TicketSlaPausePeriod>
{
    public void Configure(EntityTypeBuilder<TicketSlaPausePeriod> builder)
    {
        builder.ToTable("TicketSlaPausePeriods");

        builder.HasKey(p => p.TicketSlaPausePeriodId);
        builder.Property(p => p.TicketSlaPausePeriodId).ValueGeneratedOnAdd();

        builder.Property(p => p.TicketId).IsRequired();
        builder.Property(p => p.TicketSlaInstanceId).IsRequired();
        builder.Property(p => p.Reason).IsRequired();
        builder.Property(p => p.StartedAtUtc).IsRequired();
        builder.Property(p => p.ResumedAtUtc);
        builder.Property(p => p.ResolutionDueBeforeAtUtc).IsRequired();
        builder.Property(p => p.ResolutionDueAfterAtUtc);
        builder.Property(p => p.EndedByResolution).IsRequired();

        // At most one open pause per ticket: the database backstop behind
        // "repeated Pending Customer / duplicate resume must not double
        // count". The application checks first; two concurrent status
        // changes can both pass that check, and this index makes the loser
        // fail instead of opening a second pause.
        builder.HasIndex(p => p.TicketId)
            .IsUnique()
            .HasFilter("[ResumedAtUtc] IS NULL")
            .HasDatabaseName("UX_TicketSlaPausePeriods_OneOpenPerTicket");

        builder.HasIndex(p => p.TicketSlaInstanceId)
            .HasDatabaseName("IX_TicketSlaPausePeriods_Instance");

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_TicketSlaPausePeriods_Order",
            "[ResumedAtUtc] IS NULL OR [ResumedAtUtc] >= [StartedAtUtc]"));

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(p => p.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TicketSlaInstance>()
            .WithMany()
            .HasForeignKey(p => p.TicketSlaInstanceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
