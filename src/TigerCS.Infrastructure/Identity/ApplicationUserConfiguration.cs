using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TigerCS.Infrastructure.Identity;

/// <summary>
/// The Genesys agent identity mapping columns on <c>AspNetUsers</c>. Identity's
/// own columns are configured by <c>IdentityDbContext</c>; this adds only the
/// two mapping fields and the index that makes the mapping trustworthy.
/// </summary>
public class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.GenesysUserId).HasMaxLength(ApplicationUser.GenesysUserIdMaxLength);
        builder.Property(u => u.GenesysEmail).HasMaxLength(ApplicationUser.GenesysEmailMaxLength);

        // One Genesys agent resolves to at most ONE Ticketing user — a
        // database guarantee, not a convention. Filtered on NOT NULL because
        // most users are not Genesys agents at all, and SQL Server treats
        // multiple NULLs as duplicates in an unfiltered unique index
        // (the same pattern as UX_TicketInteractions_GenesysConversationId).
        builder.HasIndex(u => u.GenesysUserId, "UX_AspNetUsers_GenesysUserId")
            .HasFilter("[GenesysUserId] IS NOT NULL")
            .IsUnique();
    }
}
