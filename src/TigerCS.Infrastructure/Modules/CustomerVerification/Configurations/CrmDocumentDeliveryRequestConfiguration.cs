using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Infrastructure.Identity;

namespace TigerCS.Infrastructure.Modules.CustomerVerification.Configurations;

/// <summary>
/// The idempotency and delivery record for chatbot document copies. The
/// unique (caller, key) index is what makes "Genesys retried the same request"
/// a database fact: two concurrent identical requests cannot both claim the
/// send. No document content and no email address is stored — a masked
/// destination and CRM's own record id only.
/// </summary>
public class CrmDocumentDeliveryRequestConfiguration : IEntityTypeConfiguration<CrmDocumentDeliveryRequest>
{
    public void Configure(EntityTypeBuilder<CrmDocumentDeliveryRequest> builder)
    {
        builder.ToTable("CrmDocumentDeliveryRequests");  // (check constraint added below)

        builder.HasKey(r => r.CrmDocumentDeliveryRequestId);
        builder.Property(r => r.CrmDocumentDeliveryRequestId).ValueGeneratedOnAdd();

        builder.Property(r => r.CallerEmployeeId).IsRequired();
        builder.Property(r => r.IdempotencyKey).HasMaxLength(CrmDocumentDeliveryRequest.IdempotencyKeyMaxLength).IsRequired();
        builder.Property(r => r.Fingerprint).HasMaxLength(64).IsRequired();
        // Origin says which door asked. Rows that predate agent sends are all Genesys (the column default).
        builder.Property(r => r.Origin).HasConversion<byte>().HasDefaultValue(DocumentDeliveryOrigin.GenesysVerifiedSession).IsRequired();
        // Only a Genesys send has a verification session; an agent-desk send is scoped by CRM customer + lead instead.
        builder.Property(r => r.VerificationSessionId);
        builder.Property(r => r.CrmCustomerId);
        builder.Property(r => r.CrmLeadId);
        builder.Property(r => r.DocumentType).HasConversion<byte>().IsRequired();
        builder.Property(r => r.CrmRecordId).HasMaxLength(CrmDocumentDeliveryRequest.RecordIdMaxLength).IsRequired();
        builder.Property(r => r.Channel).HasConversion<byte>().IsRequired();
        builder.Property(r => r.MaskedDestination).HasMaxLength(CrmDocumentDeliveryRequest.MaskedDestinationMaxLength);
        builder.Property(r => r.Status).HasConversion<byte>().IsRequired();
        builder.Property(r => r.FailureCode).HasMaxLength(CrmDocumentDeliveryRequest.FailureCodeMaxLength);
        builder.Property(r => r.AttemptCount).IsRequired();
        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc).IsRequired();

        builder.HasIndex(r => new { r.CallerEmployeeId, r.IdempotencyKey }, "UX_CrmDocumentDeliveryRequests_CallerKey")
            .IsUnique();

        // The "same document, same session, recently sent" duplicate check.
        builder.HasIndex(
            r => new { r.VerificationSessionId, r.DocumentType, r.CrmRecordId, r.Channel, r.Status },
            "IX_CrmDocumentDeliveryRequests_SessionDocument");

        // The agent-desk equivalent of the index above: "same document, same customer and unit, recently sent".
        builder.HasIndex(
            r => new { r.CrmCustomerId, r.CrmLeadId, r.DocumentType, r.CrmRecordId, r.Channel, r.Status },
            "IX_CrmDocumentDeliveryRequests_AgentDocument");

        // The two origins are mutually exclusive in shape: a Genesys row has its real session and no agent scope; an
        // agent-desk row has its CRM customer + lead and no session. Nothing in between can be written.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_CrmDocumentDeliveryRequests_Origin",
            "([Origin] = 1 AND [VerificationSessionId] IS NOT NULL AND [CrmCustomerId] IS NULL AND [CrmLeadId] IS NULL) "
            + "OR ([Origin] = 2 AND [VerificationSessionId] IS NULL AND [CrmCustomerId] IS NOT NULL AND [CrmLeadId] IS NOT NULL)"));

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(r => r.CallerEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
