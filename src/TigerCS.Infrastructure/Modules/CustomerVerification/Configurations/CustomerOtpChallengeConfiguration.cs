using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Infrastructure.Identity;

namespace TigerCS.Infrastructure.Modules.CustomerVerification.Configurations;

/// <summary>
/// Email OTP challenges. The code itself is never stored — only a salted HMAC.
/// <c>Status</c>, <c>FailedAttempts</c> and <c>SendCount</c> are concurrency
/// tokens: a code can be spent once, and parallel wrong guesses cannot
/// out-run the attempt budget (the losing write is re-read and re-evaluated).
/// </summary>
public class CustomerOtpChallengeConfiguration : IEntityTypeConfiguration<CustomerOtpChallenge>
{
    public void Configure(EntityTypeBuilder<CustomerOtpChallenge> builder)
    {
        builder.ToTable("CustomerOtpChallenges");

        builder.HasKey(c => c.CustomerOtpChallengeId);
        builder.Property(c => c.CustomerOtpChallengeId).ValueGeneratedNever();

        builder.Property(c => c.CallerEmployeeId).IsRequired();
        builder.Property(c => c.CrmCustomerId).IsRequired();
        builder.Property(c => c.CrmLeadId).IsRequired();
        builder.Property(c => c.UnitReferenceId).IsRequired();
        builder.Property(c => c.ContactReferenceId).IsRequired();
        builder.Property(c => c.MaskedDestination).HasMaxLength(CustomerOtpChallenge.MaskedDestinationMaxLength).IsRequired();
        builder.Property(c => c.Salt).HasMaxLength(32).IsRequired();
        builder.Property(c => c.CodeHash).HasMaxLength(32).IsRequired();

        builder.Property(c => c.Status).HasConversion<byte>().IsRequired().IsConcurrencyToken();
        builder.Property(c => c.FailedAttempts).IsRequired().IsConcurrencyToken();
        builder.Property(c => c.SendCount).IsRequired().IsConcurrencyToken();
        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.LastSentAtUtc).IsRequired();
        builder.Property(c => c.ExpiresAtUtc).IsRequired();
        builder.Property(c => c.VerifiedAtUtc);
        builder.Property(c => c.VerificationSessionId);

        // "How many challenges did this customer get in the last hour" and "is there a live one for this caller + lead".
        builder.HasIndex(c => new { c.CrmCustomerId, c.CreatedAtUtc }, "IX_CustomerOtpChallenges_Customer");
        builder.HasIndex(c => new { c.CallerEmployeeId, c.CrmCustomerId, c.CrmLeadId, c.Status }, "IX_CustomerOtpChallenges_CallerLead");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(c => c.CallerEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<UnitReference>().WithMany().HasForeignKey(c => c.UnitReferenceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ContactReference>().WithMany().HasForeignKey(c => c.ContactReferenceId).OnDelete(DeleteBehavior.Restrict);
    }
}
