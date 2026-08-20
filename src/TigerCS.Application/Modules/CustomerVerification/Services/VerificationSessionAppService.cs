using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CustomerVerification.Services;

/// <summary>
/// <b>Tiger CS Ticketing's own business logic — not CRM business logic.</b>
/// Tiger CRM's entire role in verification is passive, read-only data
/// access: given a unit number, it returns the unit's immutable CRM
/// identifier and minimum display details, plus the contacts/owners/
/// tenants/authorized representatives linked to it (see
/// <see cref="TigerCS.Application.Modules.CustomerVerification.CrmIntegration.ICrmGateway"/>).
/// Everything else — the <see cref="VerificationSession"/> and its state
/// machine, <i>selecting</i> which returned contact is the requester,
/// recording the verification method/result (the verbal read-back
/// confirmation), deciding whether ticket creation may proceed, capturing
/// the immutable verification-time snapshot, and the audit/authorization/
/// expiry rules around all of it — is owned here, by Tiger CS Ticketing.
/// A future automated intake surface (e.g. Genesys AI voice/chat) that
/// collects interaction data must call Tiger CS Ticketing's own API (this
/// service, via <c>POST /api/verification-sessions</c> — a Ticketing
/// endpoint) to have a requester verified; it must never implement these
/// verification rules itself, and must never connect to a CRM system
/// directly.
///
/// <para>
/// MVP-API-Contracts.md §2.4, simplified per MVP-Implementation-Backlog.md
/// §0.2/S-07: unit/contact selection and verbal confirmation are combined
/// into a single call against a single-use VerificationSessions row
/// (MVP-ERD.md §2.24), rather than the full start/select/confirm/get
/// four-endpoint flow. The confirmed session's snapshot fields are exactly
/// what a later ticket-creation call (out of scope this phase, and itself
/// also Ticketing's own business logic — see <c>IntakeRecord</c> in
/// MVP-ERD.md §2.9, not yet built) would copy into
/// TicketRequesterSnapshots (ADR-0007).
/// </para>
/// <para>
/// <b>Idempotency — scope and upgrade path.</b> <see cref="VerificationSession.IdempotencyKey"/>
/// plus a filtered unique index on (AgentEmployeeId, IdempotencyKey)
/// (VerificationSessionConfiguration) is a pilot-scoped, module-local
/// substitute for the approved generalized <c>IdempotencyRecords</c>
/// mechanism (ADR-0014, MVP-Data-Dictionary.md §2.23), which has not been
/// built yet (backlog S-05: "Audit trail, Outbox, and idempotency
/// foundation"). It cannot conflict with that design: it lives entirely on
/// <c>VerificationSessions</c>' own column/index, shares no table, no FK,
/// and no <c>Scope</c>-string namespace with <c>IdempotencyRecords</c> or
/// <c>OutboxMessages</c>. Its scope is exactly one thing — deduplicating
/// <see cref="CreateAndConfirmAsync"/>'s create+confirm call on a
/// double-submit or a genuine concurrent race (handled below via
/// <see cref="DuplicateWriteException"/>, not a plain check-then-insert,
/// which would itself be a race). <b>Upgrade path:</b> when S-05 ships, this
/// column and its check-then-insert/race-recovery logic should be replaced
/// wholesale by a call through the generalized idempotency service against
/// <c>IdempotencyRecords</c> (Scope = e.g. "CreateVerificationSession"),
/// dropping the local column/index in the same migration — not layered
/// alongside it.
/// </para>
/// </summary>
public sealed class VerificationSessionAppService(
    IVerificationSessionRepository sessionRepository,
    IUnitReferenceRepository unitRepository,
    IContactReferenceRepository contactRepository,
    ICustomerVerificationUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    /// <summary>MVP-ERD.md §2.24 [ASSUMPTION] — 30 minutes from creation.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

    public async Task<VerificationSessionResult> CreateAndConfirmAsync(
        Guid agentEmployeeId,
        CreateVerificationSessionRequestDto request,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var replay = await sessionRepository.GetByIdempotencyKeyAsync(agentEmployeeId, idempotencyKey, cancellationToken);
            if (replay is not null)
            {
                return VerificationSessionResult.Success(ToDto(replay));
            }
        }

        var unit = await unitRepository.GetByIdAsync(request.UnitReferenceId, cancellationToken);
        var contact = await contactRepository.GetByIdAsync(request.ContactReferenceId, cancellationToken);
        if (unit is null || contact is null || contact.UnitReferenceId != unit.UnitReferenceId)
        {
            return VerificationSessionResult.UnitOrContactNotFound();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var session = new VerificationSession(
            Guid.NewGuid(),
            agentEmployeeId,
            unit.UnitReferenceId,
            contact.ContactReferenceId,
            unit.UnitNumber,
            unit.PropertyName,
            unit.TowerName,
            unit.UnitType,
            contact.DisplayName,
            contact.ContactChannel,
            now,
            now.Add(SessionLifetime),
            string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey);

        // Selection (constructor) and confirmation happen in this one
        // application-layer call — see the type-level remarks above.
        session.Confirm(now);

        await sessionRepository.AddAsync(session, cancellationToken);
        await auditWriter.WriteAsync(
            agentEmployeeId,
            "ConfirmVerificationSession",
            "VerificationSession",
            session.VerificationSessionId.ToString(),
            beforeValue: null,
            afterValue: $"UnitReferenceId={unit.UnitReferenceId};ContactReferenceId={contact.ContactReferenceId}",
            correlationId: Guid.NewGuid(),
            cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateWriteException) when (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // Two concurrent requests with the same Idempotency-Key both
            // passed the check above before either committed (the race the
            // check-then-insert above cannot close on its own) — the
            // (AgentEmployeeId, IdempotencyKey) unique index caught the
            // second writer here. The loser returns the winner's row rather
            // than surfacing a 500, which is what idempotency is for.
            var winner = await sessionRepository.GetByIdempotencyKeyAsync(agentEmployeeId, idempotencyKey, cancellationToken);
            if (winner is not null)
            {
                return VerificationSessionResult.Success(ToDto(winner));
            }

            throw;
        }

        return VerificationSessionResult.Success(ToDto(session));
    }

    public async Task<VerificationSessionResult> GetAsync(
        Guid verificationSessionId, Guid callerEmployeeId, CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.GetByIdAsync(verificationSessionId, cancellationToken);
        if (session is null)
        {
            return VerificationSessionResult.NotFound();
        }

        // Single-agent ownership (MVP-ERD.md §2.24) — no Supervisor+ override at MVP.
        if (!session.IsOwnedBy(callerEmployeeId))
        {
            return VerificationSessionResult.Forbidden();
        }

        return VerificationSessionResult.Success(ToDto(session));
    }

    private VerificationSessionResponseDto ToDto(VerificationSession session)
    {
        var effectiveStatus = session.EffectiveStatus(timeProvider.GetUtcNow().UtcDateTime);
        return new VerificationSessionResponseDto(
            session.VerificationSessionId,
            session.AgentEmployeeId,
            session.UnitReferenceId,
            session.ContactReferenceId,
            effectiveStatus.ToString(),
            // Domain's channel-neutral Confirmed maps onto the DTO's
            // approved wire field name ConfirmedVerbally (MVP-API-Contracts.md
            // §2.4.3) — see VerificationSession's type-level remarks.
            session.Confirmed,
            session.CreatedAtUtc,
            session.ConfirmedAtUtc,
            session.ExpiresAtUtc,
            session.SnapshotUnitNumber,
            session.SnapshotPropertyName,
            session.SnapshotTowerName,
            session.SnapshotUnitType,
            session.SnapshotContactDisplayName,
            session.SnapshotContactChannel);
    }
}
