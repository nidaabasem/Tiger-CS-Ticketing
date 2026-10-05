using System.Text.Json;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// Creating reminders — by an authorized user, by the Genesys outbound
/// campaign, or by the designated scheduler — and listing who is due one.
///
/// <para>
/// <b>Every reminder is decided against fresh source figures.</b> The account
/// is re-read from the financial source on every create, its eligibility and
/// amount evaluated with <see cref="ReminderPolicy"/>, and a settled account
/// is refused rather than reminded. SMS and email are then re-checked once
/// more immediately before dispatch (<see cref="CollectionsReminderDispatchHandler"/>).
/// </para>
///
/// <para>
/// <b>Duplicate prevention</b> is the unique de-duplication key
/// (account | type | cycle | channel): read-before-write here, and a unique
/// index in the database behind it, so concurrent requests produce one
/// reminder and the loser is answered with it.
/// </para>
/// </summary>
public sealed class CollectionsReminderAppService(
    CollectionsOptions options,
    CollectionsAuthorizationService authorization,
    CollectionsClock clock,
    ICollectionsFinancialSource source,
    ICollectionsReminderRepository reminderRepository,
    ICollectionsUnitOfWork unitOfWork,
    IOutboxWriter outboxWriter,
    IAuditEntryWriter auditWriter,
    ILogger<CollectionsReminderAppService> logger)
{
    public const string DispatchEventType = "CollectionsReminderQueued";
    public const string AuditEntityType = "CollectionsReminder";

    public async Task<CollectionsResult<CreateCollectionsReminderResponseDto>> CreateAsync(
        CollectionsCaller caller, CreateCollectionsReminderRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return Fail(CollectionsOutcome.Disabled);
        }

        var permissions = await authorization.ResolveAsync(caller, cancellationToken);
        if (!permissions.CanSendReminders)
        {
            return Fail(CollectionsOutcome.Forbidden, "Sending a reminder requires the Collections reminder permission.");
        }

        if (string.IsNullOrWhiteSpace(request.CrmCustomerId) || string.IsNullOrWhiteSpace(request.AccountId))
        {
            return Fail(CollectionsOutcome.ValidationFailed, "crmCustomerId and accountId are required.");
        }

        if (!CollectionsEnums.TryParse<ReminderChannel>(request.Channel, out var channel))
        {
            return Fail(CollectionsOutcome.ValidationFailed, $"channel must be one of {CollectionsEnums.Names<ReminderChannel>()}.");
        }

        var type = ReminderType.Manual;
        if (!string.IsNullOrWhiteSpace(request.ReminderType) && !CollectionsEnums.TryParse(request.ReminderType, out type))
        {
            // Legal notices and referrals are deliberately not reminder types.
            return Fail(CollectionsOutcome.ValidationFailed, $"reminderType must be one of {CollectionsEnums.Names<ReminderType>()}.");
        }

        if (!options.Channels.IsEnabled(channel))
        {
            return Fail(CollectionsOutcome.ChannelNotEnabled, $"The {channel} reminder channel is not enabled.");
        }

        // The voice bot is Genesys' to dial. A reminder recorded for it by
        // anyone else would sit Queued with nobody to place the call.
        if (channel == ReminderChannel.VoiceBot && !permissions.IsIntegration)
        {
            return Fail(CollectionsOutcome.ValidationFailed, "VoiceBot reminders are recorded by the Genesys outbound campaign itself, immediately before it dials.");
        }

        var today = clock.Today;
        var rules = clock.Rules;
        if (type != ReminderType.Manual && ReminderPolicy.OpenWindows(today, rules).All(w => w.Type != type))
        {
            return Fail(CollectionsOutcome.NotEligible, $"The {type} window is not open on {today:yyyy-MM-dd}.");
        }

        FinancialAccountSnapshot? account;
        try
        {
            var accounts = await source.GetCustomerAccountsAsync(request.CrmCustomerId.Trim(), cancellationToken);
            if (accounts is null)
            {
                return Fail(CollectionsOutcome.CustomerNotFound, $"The financial source has no customer '{request.CrmCustomerId.Trim()}'.");
            }

            account = accounts.FirstOrDefault(a =>
                string.Equals(a.AccountId, request.AccountId.Trim(), StringComparison.Ordinal)
                && string.Equals(a.CrmCustomerId, request.CrmCustomerId.Trim(), StringComparison.Ordinal));
        }
        catch (CollectionsFinancialSourceUnavailableException ex)
        {
            return Fail(CollectionsOutcome.SourceUnavailable, ex.Message);
        }

        if (account is null)
        {
            return Fail(CollectionsOutcome.AccountNotFound, $"Account '{request.AccountId.Trim()}' does not belong to customer '{request.CrmCustomerId.Trim()}'.");
        }

        var trigger = permissions.IsIntegration ? ReminderTrigger.Integration : ReminderTrigger.Manual;
        var result = await CreateForAccountAsync(account, type, channel, today, rules, trigger, caller.EmployeeId, cancellationToken);
        return result.Outcome is CollectionsOutcome.Created or CollectionsOutcome.AlreadyExists
            ? CollectionsResult<CreateCollectionsReminderResponseDto>.Ok(
                new CreateCollectionsReminderResponseDto(result.Outcome.ToString(), CollectionsMapper.ToDto(result.Value!)), result.Outcome)
            : Fail(result.Outcome, result.Detail);
    }

    public async Task<CollectionsResult<CollectionsReminderCandidatesResponseDto>> ListCandidatesAsync(
        CollectionsCaller caller, string? reminderType, string? channel, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return CollectionsResult<CollectionsReminderCandidatesResponseDto>.Fail(CollectionsOutcome.Disabled);
        }

        var permissions = await authorization.ResolveAsync(caller, cancellationToken);
        if (!permissions.CanSendReminders)
        {
            return CollectionsResult<CollectionsReminderCandidatesResponseDto>.Fail(CollectionsOutcome.Forbidden, "Listing reminder candidates requires the Collections reminder permission.");
        }

        if (!CollectionsAccountQueryAppService.ValidPaging(page, pageSize, out var pagingError))
        {
            return CollectionsResult<CollectionsReminderCandidatesResponseDto>.Fail(CollectionsOutcome.ValidationFailed, pagingError);
        }

        ReminderType? typeFilter = null;
        if (!string.IsNullOrWhiteSpace(reminderType))
        {
            if (!CollectionsEnums.TryParse<ReminderType>(reminderType, out var parsedType) || parsedType == ReminderType.Manual)
            {
                return CollectionsResult<CollectionsReminderCandidatesResponseDto>.Fail(CollectionsOutcome.ValidationFailed,
                    "reminderType must be OverdueMoreThanOneMonth, CurrentMonthDue or MonthEndFollowUp.");
            }

            typeFilter = parsedType;
        }

        var channels = Enum.GetValues<ReminderChannel>().Where(options.Channels.IsEnabled).ToList();
        if (!string.IsNullOrWhiteSpace(channel))
        {
            if (!CollectionsEnums.TryParse<ReminderChannel>(channel, out var parsedChannel))
            {
                return CollectionsResult<CollectionsReminderCandidatesResponseDto>.Fail(CollectionsOutcome.ValidationFailed,
                    $"channel must be one of {CollectionsEnums.Names<ReminderChannel>()}.");
            }

            channels = channels.Where(c => c == parsedChannel).ToList();
        }

        var today = clock.Today;
        var rules = clock.Rules;
        var windows = ReminderPolicy.OpenWindows(today, rules).Where(w => typeFilter is null || w.Type == typeFilter).ToList();

        var candidates = new List<CollectionsReminderCandidateDto>();
        var truncated = false;
        if (windows.Count > 0 && channels.Count > 0)
        {
            ScanResult scan;
            try
            {
                scan = await ScanEligibleAsync(windows, today, rules, cancellationToken);
            }
            catch (CollectionsFinancialSourceUnavailableException ex)
            {
                return CollectionsResult<CollectionsReminderCandidatesResponseDto>.Fail(CollectionsOutcome.SourceUnavailable, ex.Message);
            }

            truncated = scan.Truncated;
            var keys = scan.Eligible
                .SelectMany(e => channels.Select(c => CollectionsReminder.BuildDeduplicationKey(e.Account.AccountId, e.Window.Type, e.Window.CycleKey, c)))
                .ToList();
            var existing = await reminderRepository.GetExistingDeduplicationKeysAsync(keys, cancellationToken);

            foreach (var (account, window, eligibility) in scan.Eligible)
            {
                var pending = channels
                    .Where(c => !existing.Contains(CollectionsReminder.BuildDeduplicationKey(account.AccountId, window.Type, window.CycleKey, c)))
                    .Select(c => c.ToString())
                    .ToList();

                if (pending.Count > 0)
                {
                    candidates.Add(new CollectionsReminderCandidateDto(
                        account.CrmCustomerId, account.AccountId, account.CrmUnitId, account.UnitNumber, account.ProjectName,
                        account.CustomerName, account.CustomerPhone, window.Type.ToString(), window.CycleKey,
                        eligibility.Amount!.Value, eligibility.Currency, account.AsOfUtc, pending));
                }
            }
        }

        var ordered = candidates
            .OrderBy(c => c.ReminderType, StringComparer.Ordinal)
            .ThenBy(c => c.CrmCustomerId, StringComparer.Ordinal)
            .ThenBy(c => c.AccountId, StringComparer.Ordinal)
            .ToList();

        return CollectionsResult<CollectionsReminderCandidatesResponseDto>.Ok(new CollectionsReminderCandidatesResponseDto(
            today,
            windows.Select(w => w.Type.ToString()).ToList(),
            source.SourceName,
            ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            ordered.Count,
            page,
            pageSize,
            truncated));
    }

    /// <summary>
    /// The designated scheduler's run: for each window open today, every
    /// eligible account gets one reminder per scheduled channel TigerCS
    /// dispatches itself. Refuses to do anything unless automatic scheduling
    /// is active — which requires Collections' confirmation of the open rule
    /// decisions as well as the switch.
    /// </summary>
    public async Task<ScheduledRunResult> RunScheduledAsync(CancellationToken cancellationToken = default)
    {
        if (!options.IsAutomaticSchedulingActive)
        {
            return new ScheduledRunResult(Ran: false, 0, 0, 0, Truncated: false,
                "Automatic scheduling is off: it needs Collections:Enabled, Collections:AutomaticSchedulingEnabled and Collections:BusinessRulesConfirmed.");
        }

        var today = clock.Today;
        var rules = clock.Rules;
        var windows = ReminderPolicy.OpenWindows(today, rules);
        var channels = options.Channels.ScheduledChannels
            .Distinct()
            .Where(c => c != ReminderChannel.VoiceBot && options.Channels.IsEnabled(c))
            .ToList();

        if (windows.Count == 0 || channels.Count == 0)
        {
            return new ScheduledRunResult(true, 0, 0, 0, false, windows.Count == 0 ? "No reminder window is open today." : "No scheduled channel is enabled.");
        }

        var scan = await ScanEligibleAsync(windows, today, rules, cancellationToken);
        int created = 0, existing = 0, refused = 0;
        foreach (var (account, window, _) in scan.Eligible)
        {
            foreach (var channel in channels)
            {
                var result = await CreateForAccountAsync(account, window.Type, channel, today, rules, ReminderTrigger.Scheduled, null, cancellationToken);
                switch (result.Outcome)
                {
                    case CollectionsOutcome.Created: created++; break;
                    case CollectionsOutcome.AlreadyExists: existing++; break;
                    default: refused++; break;
                }
            }
        }

        return new ScheduledRunResult(true, created, existing, refused, scan.Truncated, null);
    }

    private async Task<CollectionsResult<CollectionsReminder>> CreateForAccountAsync(
        FinancialAccountSnapshot account, ReminderType type, ReminderChannel channel, DateOnly today, ReminderRuleSettings rules,
        ReminderTrigger trigger, Guid? requestedBy, CancellationToken cancellationToken)
    {
        var cycleKey = ReminderPolicy.CycleKey(type, today, rules);
        var key = CollectionsReminder.BuildDeduplicationKey(account.AccountId, type, cycleKey, channel);

        // Duplicate first: an existing reminder is answered as such even if
        // the account has since been settled — what was sent stays the answer.
        if (await reminderRepository.GetByDeduplicationKeyAsync(key, cancellationToken) is { } existing)
        {
            return CollectionsResult<CollectionsReminder>.Ok(existing, CollectionsOutcome.AlreadyExists);
        }

        var eligibility = ReminderPolicy.Evaluate(account, type, today, rules);
        if (!eligibility.IsEligible)
        {
            return CollectionsResult<CollectionsReminder>.Fail(CollectionsOutcome.NotEligible,
                eligibility.Reason == ReminderPolicy.SettledReason
                    ? $"Account '{account.AccountId}' has nothing outstanding for a {type} reminder — no reminder is sent for a settled account."
                    : $"Account '{account.AccountId}' is not eligible for a {type} reminder ({eligibility.Reason}).");
        }

        if (channel == ReminderChannel.Sms && string.IsNullOrWhiteSpace(account.CustomerPhone)
            || channel == ReminderChannel.Email && string.IsNullOrWhiteSpace(account.CustomerEmail))
        {
            return CollectionsResult<CollectionsReminder>.Fail(CollectionsOutcome.NotEligible,
                $"The financial source holds no {(channel == ReminderChannel.Sms ? "phone number" : "email address")} for account '{account.AccountId}'.");
        }

        var now = clock.UtcNow;
        var reminder = new CollectionsReminder(
            account.CrmCustomerId, account.AccountId, account.CrmUnitId, type, channel, cycleKey,
            eligibility.Currency, eligibility.Amount!.Value, rules.IncludeFinesInReminderAmount, account.AsOfUtc,
            trigger, requestedBy, now);

        await reminderRepository.AddAsync(reminder, cancellationToken);

        var correlationId = Guid.NewGuid();
        if (channel != ReminderChannel.VoiceBot)
        {
            // Written in the same transaction as the reminder, so a queued
            // SMS/email can never be lost between "recorded" and "sent".
            await outboxWriter.WriteAsync(
                DispatchEventType,
                JsonSerializer.Serialize(new DispatchPayload(key)),
                correlationId,
                idempotencyKey: $"collections-reminder-dispatch:{key}",
                now,
                cancellationToken);
        }

        await auditWriter.WriteAsync(
            requestedBy, "CollectionsReminderQueued", AuditEntityType, CollectionsMapper.AuditEntityId(key), null,
            $"Type={type};Channel={channel};Amount={eligibility.Amount} {eligibility.Currency};Trigger={trigger};SourceAsOfUtc={account.AsOfUtc:O}",
            correlationId, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateWriteException)
        {
            // A concurrent request won the unique de-duplication key.
            unitOfWork.DiscardPendingChanges();
            var winner = await reminderRepository.GetByDeduplicationKeyAsync(key, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            logger.LogInformation("Concurrent reminder for {DeduplicationKey} resolved to the existing reminder {ReminderId}.", key, winner.CollectionsReminderId);
            return CollectionsResult<CollectionsReminder>.Ok(winner, CollectionsOutcome.AlreadyExists);
        }

        return CollectionsResult<CollectionsReminder>.Ok(reminder, CollectionsOutcome.Created);
    }

    private sealed record ScanResult(List<(FinancialAccountSnapshot Account, ReminderWindow Window, ReminderEligibility Eligibility)> Eligible, bool Truncated);

    private async Task<ScanResult> ScanEligibleAsync(
        IReadOnlyList<ReminderWindow> windows, DateOnly today, ReminderRuleSettings rules, CancellationToken cancellationToken)
    {
        const int sourcePageSize = 200;
        var eligible = new List<(FinancialAccountSnapshot, ReminderWindow, ReminderEligibility)>();
        var read = 0;
        var page = 1;
        var truncated = false;

        while (true)
        {
            var batch = await source.ListAccountsWithOutstandingPrincipalAsync(page, sourcePageSize, cancellationToken);
            foreach (var account in batch.Accounts)
            {
                foreach (var window in windows)
                {
                    var eligibility = ReminderPolicy.Evaluate(account, window.Type, today, rules);
                    if (eligibility.IsEligible)
                    {
                        eligible.Add((account, window, eligibility));
                    }
                }
            }

            read += batch.Accounts.Count;
            if (!batch.HasMore || batch.Accounts.Count == 0)
            {
                break;
            }

            if (read >= options.MaxAccountsPerScan)
            {
                truncated = true;
                logger.LogWarning("Collections reminder scan stopped at {MaxAccountsPerScan} accounts; more remain in the source.", options.MaxAccountsPerScan);
                break;
            }

            page++;
        }

        return new ScanResult(eligible, truncated);
    }

    private static CollectionsResult<CreateCollectionsReminderResponseDto> Fail(CollectionsOutcome outcome, string? detail = null) =>
        CollectionsResult<CreateCollectionsReminderResponseDto>.Fail(outcome, detail);

    internal sealed record DispatchPayload(string DeduplicationKey);
}

public sealed record ScheduledRunResult(bool Ran, int Created, int AlreadyExisted, int Refused, bool Truncated, string? Detail);
