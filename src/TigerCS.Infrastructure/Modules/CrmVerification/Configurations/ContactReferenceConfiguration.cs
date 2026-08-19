using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.CrmVerification;

namespace TigerCS.Infrastructure.Modules.CrmVerification.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.7 / MVP-ERD.md §2.7.</summary>
public class ContactReferenceConfiguration : IEntityTypeConfiguration<ContactReference>
{
    public void Configure(EntityTypeBuilder<ContactReference> builder)
    {
        builder.ToTable("ContactReferences");

        builder.HasKey(c => c.ContactReferenceId);
        builder.Property(c => c.ContactReferenceId).ValueGeneratedOnAdd();

        builder.Property(c => c.CrmContactId).HasMaxLength(64).IsRequired();
        builder.HasIndex(c => c.CrmContactId).IsUnique();

        builder.Property(c => c.DisplayName).HasMaxLength(200);
        builder.Property(c => c.ContactChannel).HasMaxLength(200);
        builder.Property(c => c.ContactType).IsRequired();
        builder.Property(c => c.LastSyncedAtUtc).IsRequired();

        // Self-ref (ISSUE-007's CRM-recorded-authorization requirement) — no
        // navigation property needed on the Domain side.
        builder.HasOne<ContactReference>()
            .WithMany()
            .HasForeignKey(c => c.AuthorizedRepresentativeOfContactReferenceId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
