using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CrmVerification.Abstractions;
using TigerCS.Application.Modules.CrmVerification.Dto;
using TigerCS.Domain.Modules.CrmVerification;

namespace TigerCS.Application.Modules.CrmVerification.Services;

/// <summary>
/// MVP-API-Contracts.md §2.4, simplified per MVP-Implementation-Backlog.md
/// §0.2/S-07: unit/contact selection and verbal confirmation are combined
/// into a single call against a single-use VerificationSessions row
/// (MVP-ERD.md §2.24), rather than the full start/select/confirm/get
/// four-endpoint flow. The confirmed session's snapshot fields are exactly
/// what a later ticket-creation call (out of scope this phase) would copy
/// into TicketRequesterSnapshots (ADR-0007).
/// </summary>
public sealed class VerificationSessionAppService(
    IVerificationSessionRepository sessionRepository,
    IUnitReferenceRepository unitRepository,
    IContactReferenceRepository contactRepository,
    ICrmVerificationUnitOfWork unitOfWork,
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

        // Selection (constructor) and verbal confirmation happen in this one
        // application-layer call — see the type-level remarks above.
        session.ConfirmVerbally(now);

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
        await unitOfWork.SaveChangesAsync(cancellationToken);

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
            session.ConfirmedVerbally,
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
