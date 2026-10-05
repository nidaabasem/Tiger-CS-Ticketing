using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>Who is calling: the employee, their roles and their department memberships, read from the token by the API edge.</summary>
public sealed record CollectionsCaller(Guid EmployeeId, IReadOnlyCollection<string> Roles, IReadOnlyCollection<int> DepartmentIds);

public enum CollectionsOutcome
{
    Success,
    Created,
    AlreadyExists,

    /// <summary>Recorded, but a ticket it owes is still pending and will be retried durably.</summary>
    Accepted,

    Disabled,
    Forbidden,
    ValidationFailed,
    CustomerNotFound,
    AccountNotFound,
    ReminderNotFound,
    SourceUnavailable,
    NotEligible,
    ChannelNotEnabled,
    Conflict
}

public sealed record CollectionsResult<T>(CollectionsOutcome Outcome, T? Value = default, string? Detail = null)
{
    public static CollectionsResult<T> Ok(T value, CollectionsOutcome outcome = CollectionsOutcome.Success) => new(outcome, value);

    public static CollectionsResult<T> Fail(CollectionsOutcome outcome, string? detail = null) => new(outcome, default, detail);
}

public sealed record CollectionsPermissions(bool CanReadFinancials, bool CanSendReminders, bool CanReportOutcomes, bool IsIntegration);

/// <summary>
/// Explicit financial authorization, separate from ticket permissions (which
/// this module does not touch). Reading balances, sending reminders and
/// reporting delivery outcomes are three grants; see
/// <see cref="CollectionsAuthorizationOptions"/> for who holds each. The
/// System Administrator override (ADR-0024) applies here exactly as it does
/// to every other check, through <see cref="AuthorizationGate"/>.
/// </summary>
public sealed class CollectionsAuthorizationService(CollectionsOptions options, IDepartmentRepository departmentRepository)
{
    public async Task<CollectionsPermissions> ResolveAsync(CollectionsCaller caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var auth = options.Authorization;

        var isIntegration = auth.IntegrationEmployeeIds.Contains(caller.EmployeeId);
        var holdsAny = (IEnumerable<string> roles) => caller.Roles.Any(r => roles.Contains(r, StringComparer.Ordinal));

        var isCollectionsMember = false;
        if (holdsAny(auth.CollectionsDepartmentRoles) && caller.DepartmentIds.Count > 0)
        {
            var departments = await departmentRepository.ListAsync(activeOnly: true, cancellationToken);
            var collections = departments.FirstOrDefault(d =>
                string.Equals(d.Code, options.CollectionsDepartmentCode, StringComparison.OrdinalIgnoreCase));
            isCollectionsMember = collections is not null && caller.DepartmentIds.Contains(collections.DepartmentId);
        }

        var canSend = AuthorizationGate.Evaluate(caller.Roles, () => isIntegration || isCollectionsMember || holdsAny(auth.ReminderSendRoles));
        var canRead = AuthorizationGate.Evaluate(caller.Roles, () => canSend || holdsAny(auth.FinancialReadRoles));
        var canReport = AuthorizationGate.Evaluate(caller.Roles, () => isIntegration);

        return new CollectionsPermissions(canRead, canSend, canReport, isIntegration);
    }
}

/// <summary>The business date, freshness and rule settings every Collections decision shares.</summary>
public sealed class CollectionsClock(CollectionsOptions options, TimeProvider timeProvider)
{
    private readonly Lazy<TimeZoneInfo> _zone = new(() => TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId));

    public DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, _zone.Value));

    public bool IsStale(DateTime asOfUtc) => UtcNow - asOfUtc > TimeSpan.FromMinutes(Math.Max(1, options.StaleAfterMinutes));

    public ReminderRuleSettings Rules => options.Rules.ToSettings();
}

public static class CollectionsEnums
{
    /// <summary>Strict, name-only parsing: a number or an unknown name is refused rather than coerced.</summary>
    public static bool TryParse<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        return !string.IsNullOrWhiteSpace(value)
            && !char.IsDigit(value.Trim()[0])
            && !value.Trim().StartsWith('-')
            && Enum.TryParse(value.Trim(), ignoreCase: true, out result)
            && Enum.IsDefined(result);
    }

    public static string Names<TEnum>() where TEnum : struct, Enum => string.Join(", ", Enum.GetNames<TEnum>());
}

internal static class CollectionsMapper
{
    /// <summary>
    /// The audit entity id for a reminder: its de-duplication key, which is
    /// readable and stable before the row has an id. AuditEntries.EntityId
    /// holds 100 characters and a key with a long account id can exceed that,
    /// so an over-long key keeps its readable prefix plus a hash of the whole.
    /// </summary>
    public static string AuditEntityId(string deduplicationKey)
    {
        const int max = 100;
        if (deduplicationKey.Length <= max)
        {
            return deduplicationKey;
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(deduplicationKey)))[..16];
        return $"{deduplicationKey[..(max - 17)]}~{hash}";
    }

    public static CollectionsReminderDto ToDto(CollectionsReminder r) => new(
        r.CollectionsReminderId, r.CrmCustomerId, r.AccountId, r.CrmUnitId,
        r.Type.ToString(), r.Channel.ToString(), r.CycleKey, r.Status.ToString(), r.StatusReason,
        r.Amount, r.Currency, r.AmountIncludesFines, r.SourceAsOfUtc, r.DispatchAmount, r.Trigger.ToString(),
        r.CreatedAtUtc, r.SentAtUtc, r.DeliveredAtUtc, r.FailedAtUtc, r.SuppressedAtUtc,
        r.Events.OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.CollectionsReminderEventId).Select(ToDto).ToList());

    public static CollectionsReminderEventDto ToDto(CollectionsReminderEvent e) => new(
        e.CollectionsReminderEventId, e.ExternalEventId, e.EventType.ToString(), e.OccurredAtUtc, e.RecordedAtUtc,
        e.Detail, e.ResponseKind?.ToString(), e.ConversationId, e.PromisedPaymentDate, e.PromisedAmount,
        e.VerificationFollowUpRequired, e.HumanFollowUpRequired, e.TicketStatus?.ToString(), e.TicketId, e.TicketNumber);
}
