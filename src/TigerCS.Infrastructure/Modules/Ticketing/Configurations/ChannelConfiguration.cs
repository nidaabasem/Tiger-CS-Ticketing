using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>
/// Channel Management — the configurable channel catalogue that replaced the
/// fixed <c>Channel</c> enum. The key stays <c>tinyint</c> (the enum's
/// underlying type) so <c>IntakeRecords.ChannelId</c> and
/// <c>TicketInteractions.ChannelId</c> keep their existing column type and
/// values, and the five original members are seeded with their exact ids
/// (<see cref="WellKnownChannels"/>). Never hard-deleted: both referencing
/// FKs are Restrict; <see cref="Channel.IsActive"/> is the retirement path.
/// </summary>
public class ChannelConfiguration : IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> builder)
    {
        builder.ToTable("Channels");

        builder.HasKey(c => c.ChannelId);
        builder.Property(c => c.ChannelId).ValueGeneratedOnAdd();

        builder.Property(c => c.Name).HasMaxLength(Channel.NameMaxLength).IsRequired();

        builder.Property(c => c.Code).HasMaxLength(Channel.CodeMaxLength).IsRequired();
        builder.HasIndex(c => c.Code).IsUnique();

        builder.Property(c => c.RequiresPhone).IsRequired();
        builder.Property(c => c.IsGenesysEnabled).IsRequired();
        // No database defaults, deliberately: the domain constructor
        // supplies IsActive = true / DisplayOrder = 0, and a database default
        // on a bool would make EF Core substitute it for an explicit
        // `false` (the CLR default) when an administrator adds an inactive
        // channel. Same convention as DepartmentConfiguration.
        builder.Property(c => c.IsActive).IsRequired();
        builder.Property(c => c.DisplayOrder).IsRequired();
    }
}
