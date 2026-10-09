using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CrmDocuments.Abstractions;

public interface ICustomerOtpChallengeRepository
{
    /// <summary>The challenge, tracked so the caller can change it, or null.</summary>
    Task<CustomerOtpChallenge?> GetByIdAsync(Guid challengeId, CancellationToken cancellationToken = default);

    /// <summary>The caller's still-pending, unexpired challenge for this customer and lead, or null — what makes a retried "send" a no-op instead of a second email.</summary>
    Task<CustomerOtpChallenge?> FindPendingAsync(
        Guid callerEmployeeId, int crmCustomerId, int crmLeadId, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>How many challenges (any caller) were started for this CRM customer since <paramref name="sinceUtc"/>.</summary>
    Task<int> CountStartedSinceAsync(int crmCustomerId, DateTime sinceUtc, CancellationToken cancellationToken = default);

    Task AddAsync(CustomerOtpChallenge challenge, CancellationToken cancellationToken = default);
}

/// <summary>Source of one-time codes — abstracted so tests can use known codes. The default is a cryptographic 6-digit generator.</summary>
public interface IOtpCodeGenerator
{
    string NewCode();
}
