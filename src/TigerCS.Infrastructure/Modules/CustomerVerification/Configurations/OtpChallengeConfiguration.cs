using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Infrastructure.Modules.CustomerVerification.Configurations;

/// <summary>
/// <c>OtpChallenges</c>: one row per issued code request. Only the HMAC of the code is stored;
/// <c>Version</c> is the optimistic-concurrency token that stops two simultaneous verifies or resends both winning.
/// </summary>
public class OtpChallengeConfiguration : IEntityTypeConfiguration<OtpChallenge>
{
    public void Configure(EntityTypeBuilder<OtpChallenge> builder)
    {
        builder.ToTable("OtpChallenges");

        builder.HasKey(c => c.OtpChallengeId);
        builder.Property(c => c.OtpChallengeId).ValueGeneratedNever();

        builder.Property(c => c.OwnerEmployeeId).IsRequired();
        builder.Property(c => c.CrmCustomerId).IsRequired();
        builder.Property(c => c.CrmUnitId).IsRequired();
        builder.Property(c => c.UnitNumber).HasMaxLength(64);
        builder.Property(c => c.ProjectName).HasMaxLength(256);
        builder.Property(c => c.Channel).HasMaxLength(OtpChallenge.ChannelMaxLength).IsRequired();
        builder.Property(c => c.Destination).HasMaxLength(OtpChallenge.DestinationMaxLength).IsRequired();
        builder.Property(c => c.Language).HasMaxLength(2).IsRequired();
        builder.Property(c => c.CodeHash).HasMaxLength(OtpChallenge.CodeHashLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.ExpiresAtUtc).IsRequired();
        builder.Property(c => c.LastSentAtUtc).IsRequired();
        builder.Property(c => c.Status).HasConversion<int>().IsRequired();
        builder.Property(c => c.DeliveryState).HasConversion<int>().IsRequired();
        builder.Property(c => c.Version).IsConcurrencyToken();

        builder.HasIndex(c => new { c.CrmCustomerId, c.CreatedAtUtc }).HasDatabaseName("IX_OtpChallenges_Customer_CreatedAt");
    }
}
