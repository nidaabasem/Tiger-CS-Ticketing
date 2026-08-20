using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>
/// A supplemental, Ticketing-module-owned configuration for
/// <see cref="VerificationSession"/> — deliberately not folded into
/// <c>VerificationSessionConfiguration</c> (the CustomerVerification
/// module's own file, which this increment's instructions say not to
/// modify). EF Core composes multiple <c>IEntityTypeConfiguration&lt;T&gt;</c>
/// registrations for the same entity type additively, so this adds exactly
/// one thing — <c>Status</c> as an optimistic-concurrency token — without
/// touching CustomerVerification's file, tests, or any of its own
/// behavior (session creation/confirmation/expiry are all unaffected).
///
/// <para>
/// <b>Why this is needed:</b> without it, two concurrent
/// <c>POST /api/tickets</c> requests referencing the same already-
/// <c>Confirmed</c> session can each independently pass
/// <c>VerificationSession.Consume()</c>'s in-memory check (neither has
/// committed yet) and both persist successfully — silently violating
/// MVP-ERD.md §2.24's single-use invariant and creating two tickets from
/// one verification. Marking <c>Status</c> a concurrency token makes EF
/// Core include it in the generated <c>UPDATE ... WHERE Status = @original</c>
/// clause; the loser's update affects zero rows and EF Core throws
/// <c>DbUpdateConcurrencyException</c>, which <c>TicketingUnitOfWork</c>
/// translates to <c>VerificationSessionConcurrentlyConsumedException</c>.
/// </para>
/// <para>
/// <b>No schema/migration impact.</b> This is a pure query-generation
/// change (which columns appear in the WHERE clause of UPDATE/DELETE
/// statements) — <c>VerificationSessions.Status</c> is already an existing,
/// unchanged <c>int NOT NULL</c> column; no new column, index, or DDL is
/// introduced. <c>TigerCsDbContextModelSnapshot.cs</c> is updated to match
/// so <c>dotnet ef migrations has-pending-model-changes</c> stays accurate.
/// </para>
/// </summary>
public class VerificationSessionConsumptionConcurrencyConfiguration : IEntityTypeConfiguration<VerificationSession>
{
    public void Configure(EntityTypeBuilder<VerificationSession> builder)
    {
        builder.Property(s => s.Status).IsConcurrencyToken();
    }
}
