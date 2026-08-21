using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.17 / MVP-ERD.md §2.17 — escalation events; never deleted.</summary>
public class TicketEscalationConfiguration : IEntityTypeConfiguration<TicketEscalation>
{
    public void Configure(EntityTypeBuilder<TicketEscalation> builder)
    {
        builder.ToTable("TicketEscalations");

        builder.HasKey(e => e.TicketEscalationId);
        builder.Property(e => e.TicketEscalationId).ValueGeneratedOnAdd();

        builder.Property(e => e.TicketId).IsRequired();
        builder.Property(e => e.Level).IsRequired();
        builder.Property(e => e.TriggerType).IsRequired();
        builder.Property(e => e.NotifiedRoles).HasMaxLength(200);
        builder.Property(e => e.RaisedAtUtc).IsRequired();
        builder.Property(e => e.RespondedAtUtc);

        builder.HasIndex(e => new { e.TicketId, e.RaisedAtUtc })
            .HasDatabaseName("IX_TicketEscalations_TicketRaisedAt");

        // The database backstop behind backlog S-11's "exactly one Level 2
        // escalation, not a repeating one on every subsequent check". The
        // application checks first, but two concurrent detection paths — the
        // scheduled job of SLA-Architecture.md §13 and the sweep of §14 —
        // can both pass that check before either commits. Filtered to
        // TriggerType = AutoBreach (1) so it constrains only the automatic
        // path: a ticket may still carry several manual escalations as it
        // moves up the Level 1 → 4 hierarchy.
        builder.HasIndex(e => e.TicketId)
            .IsUnique()
            .HasFilter("[TriggerType] = 1")
            .HasDatabaseName("UX_TicketEscalations_OneAutoBreachPerTicket");

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_TicketEscalations_LevelRange", "[Level] BETWEEN 1 AND 4"));

        // Level 4 is manual-only (Solution-Analysis.md §7.6): no automatic
        // trigger type may ever produce one, and ManualLevel4 (4) exists for
        // no other level. Enforced here as well as in the application so the
        // rule survives any future writer; the CS-Manager/GM role gate stays
        // app-level, since the database has no concept of a role
        // (MVP-ERD.md §2.17).
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_TicketEscalations_Level4IsManualOnly",
            "([Level] = 4 AND [TriggerType] = 4) OR ([Level] <> 4 AND [TriggerType] <> 4)"));

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(e => e.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(e => e.RespondingEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
