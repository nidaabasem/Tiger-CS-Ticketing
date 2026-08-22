using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Web.Infrastructure;

namespace TigerCS.Web.Services;

/// <summary>
/// <c>POST /api/verification-sessions</c> - S-07's combined
/// create-select-confirm step.
///
/// <para>
/// A session belongs to the agent who created it, is single-use, and expires.
/// This client never sends <c>confirmed: false</c>: the API rejects it with
/// 400, and the confirmation gesture is a hard gate in the wizard rather than
/// something to be discovered server-side.
/// </para>
/// </summary>
public sealed class VerificationSessionsApiClient(TigerCsApiClient api)
{
    /// <summary>Creates an already-confirmed session over a looked-up unit and one of its contacts.</summary>
    public Task<ApiResult<VerificationSessionResponseDto>> CreateConfirmedAsync(
        int unitReferenceId, int contactReferenceId, CancellationToken cancellationToken) =>
        api.PostAsync<VerificationSessionResponseDto>(
            "/api/verification-sessions",
            new CreateVerificationSessionRequestDto(
                unitReferenceId,
                contactReferenceId,
                Confirmed: true,
                // The only method this pilot supports. The others exist in the
                // DTO so a future channel needs no API change; none of them is
                // reachable from a phone-intake screen.
                VerificationMethod: "ManualAgentConfirmation"),
            cancellationToken);

    /// <summary>Reads a session back. Only the owning agent may.</summary>
    public Task<ApiResult<VerificationSessionResponseDto>> GetAsync(
        Guid verificationSessionId, CancellationToken cancellationToken) =>
        api.GetAsync<VerificationSessionResponseDto>(
            $"/api/verification-sessions/{verificationSessionId}", cancellationToken);
}
