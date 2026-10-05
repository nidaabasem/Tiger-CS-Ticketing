using System.Text.Json;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// Reminder candidates and the queue.
///
/// <para>
/// <b>Candidate first.</b> A reminder can only be queued from a candidate —
/// an eligible account, its qualifying instalments and amount, computed from
/// a fresh, non-stale source read. Queueing re-reads the account and accepts
/// the candidate only if it is unexpired and a fresh evaluation reproduces it
/// exactly; otherwise <c>409 CandidateChanged</c> with a replacement. No
/// client ever supplies a balance or a destination.
/// </para>
///
/// <para>
/// <b>No duplicates.</b> Each channel's account | type | cycle | channel key is
/// unique in the database; an Idempotency-Key replays the original job (or
/// conflicts when reused with a different body) and never bypasses cycle
/// de-duplication. Only a failed channel may be retried, within its attempt
/// budget.
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
    public const string DispatchEventType = "CollectionsReminderChannelQueued";
    public const string AuditEntityType = "CollectionsReminder";
    private static readonly string[] Languages = ["en", "ar"];

    // ------------------------------------------------------------------
    // Candidates
    // ------------------------------------------------------------------

    public async Task<CollectionsResult<CollectionsReminderCandidatesResponseDto>> ListCandidatesAsync(
        CollectionsCaller caller, string? reminderType, DateOnly? businessDate, long? crmCustomerId, string? accountId,
        string? cursor, int? pageSize, CancellationToken cancellationToken = default)
    {
        if (await SendGateAsync<CollectionsReminderCandidatesResponseDto>(caller, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (!CollectionsEnums.TryParse<ReminderType>(reminderType, out var type))
        {
            return CollectionsResult<CollectionsReminderCandidatesResponseDto>.Fail(CollectionsOutcome.InvalidRequest,
                $"reminderType is required: {CollectionsEnums.Names<ReminderType>()}.");
        }

        var today = clock.BusinessDate;
        if (businessDate is { } requested && requested != today)
        {
            return CollectionsResult<CollectionsReminderCandidatesResponseDto>.Fail(CollectionsOutcome.InvalidRequest,
                $"Candidates are issued only for the current business date ({today:yyyy-MM-dd}, {clock.TimeZoneId}).");
        }

        if (!CollectionsCursor.TryRead(cursor, pageSize, out var offset, out var size, out var pagingError))
        {
            return CollectionsResult<CollectionsReminderCandidatesResponseDto>.Fail(CollectionsOutcome.InvalidRequest, pagingError);
        }

        var rules = clock.Rules;
        var cycleKey = ReminderPolicy.CycleKey(type, today, rules);
        var windowOpen = ReminderPolicy.OpenWindows(today, rules).Any(w => w.Type == type);

        var candidates = new List<CollectionsReminderCandidateDto>();
        if (windowOpen)
        {
            IReadOnlyList<FinancialAccountSnapshot> accounts;
            try
            {
                accounts = await ScopeAsync(crmCustomerId, accountId, cancellationToken);
            }
            catch (CollectionsFinancialSourceUnavailableException ex)
            {
                return CollectionsResult<CollectionsReminderCandidatesResponseDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message);
            }

            var offers = new List<(FinancialAccountSnapshot Account, ReminderEligibility Eligibility)>();
            foreach (var account in accounts)
            {
                // A stale read never authorizes a send.
                if (clock.IsStale(account.AsOfUtc))
                {
                    continue;
                }

                var eligibility = ReminderPolicy.Evaluate(account, type, today, rules);
                if (eligibility.IsEligible)
                {
                    offers.Add((account, eligibility));
                }
            }

            var existing = await ExistingChannelsAsync(offers.Select(o => o.Account), type, cycleKey, cancellationToken);
            foreach (var (account, eligibility) in offers)
            {
                var candidate = ToCandidateDto(account, type, cycleKey, eligibility, existing);
                if (candidate.AvailableChannels.Count > 0)
                {
                    candidates.Add(candidate);
                }
            }
        }

        var ordered = candidates.OrderBy(c => c.CrmCustomerId).ThenBy(c => c.AccountId, StringComparer.Ordinal).ToList();
        return CollectionsResult<CollectionsReminderCandidatesResponseDto>.Ok(new CollectionsReminderCandidatesResponseDto(
            type.ToString(), cycleKey, today, clock.TimeZoneId, windowOpen,
            ordered.Skip(offset).Take(size).ToList(),
            CollectionsCursor.Next(offset, size, offset + size < ordered.Count)));
    }

    // ------------------------------------------------------------------
    // Queue
    // ------------------------------------------------------------------

    public async Task<CollectionsResult<CollectionsReminderJobDto>> QueueAsync(
        CollectionsCaller caller, QueueCollectionsReminderRequestDto request, string? idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await SendGateAsync<CollectionsReminderJobDto>(caller, cancellationToken) is { } refused)
        {
            return refused;
        }

        var permissions = await authorization.ResolveAsync(caller, cancellationToken);

        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (key is { Length: > CollectionsReminder.IdempotencyKeyMaxLength })
        {
            return Fail(CollectionsOutcome.InvalidRequest, $"Idempotency-Key is at most {CollectionsReminder.IdempotencyKeyMaxLength} characters.");
        }

        var channels = new List<ReminderChannel>();
        foreach (var name in request.Channels ?? [])
        {
            if (!CollectionsEnums.TryParse<ReminderChannel>(name, out var channel))
            {
                return Fail(CollectionsOutcome.InvalidRequest, $"channels must be from {CollectionsEnums.Names<ReminderChannel>()}.");
            }

            if (!channels.Contains(channel))
            {
                channels.Add(channel);
            }
        }

        var language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language.Trim().ToLowerInvariant();
        var requestHash = CollectionsHashing.Hash(new
        {
            candidateId = request.CandidateId?.Trim(),
            channels = channels.Select(c => c.ToString()).Order(StringComparer.Ordinal).ToArray(),
            language
        });

        if (key is not null && await reminderRepository.GetByIdempotencyKeyAsync(key, cancellationToken) is { } original)
        {
            return original.RequestHash == requestHash
                ? CollectionsResult<CollectionsReminderJobDto>.Ok(CollectionsMapper.ToJobDto(original), CollectionsOutcome.Replayed)
                : Fail(CollectionsOutcome.IdempotencyConflict, "This Idempotency-Key was already used with a different request.");
        }

        if (!CollectionsCandidate.TryDecode(request.CandidateId, out var candidate))
        {
            return Fail(CollectionsOutcome.InvalidRequest, "candidateId is required and must come from the candidates list.");
        }

        if (channels.Count == 0)
        {
            return Fail(CollectionsOutcome.InvalidRequest, "channels must name at least one channel.");
        }

        if (!Languages.Contains(language))
        {
            return Fail(CollectionsOutcome.InvalidRequest, "language must be \"en\" or \"ar\".");
        }

        var disabled = channels.Where(c => !options.Channels.IsEnabled(c)).ToList();
        if (disabled.Count > 0)
        {
            return Fail(CollectionsOutcome.ChannelNotEnabled, $"Reminder channel(s) not enabled: {string.Join(", ", disabled)}.");
        }

        // The voice bot is Genesys' to dial; a VoiceBot channel queued by
        // anyone else would wait for a call nobody places.
        if (channels.Contains(ReminderChannel.VoiceBot) && !permissions.IsIntegration)
        {
            return Fail(CollectionsOutcome.Forbidden, "VoiceBot reminders are queued by the Genesys integration account, which places the call.");
        }

        // Fresh read, then the candidate must reproduce exactly.
        FinancialAccountSnapshot? account;
        try
        {
            account = (await source.GetCustomerAccountsAsync(candidate!.CrmCustomerId, cancellationToken))?
                .FirstOrDefault(a => a.AccountId == candidate.AccountId && a.CrmCustomerId == candidate.CrmCustomerId);
        }
        catch (CollectionsFinancialSourceUnavailableException ex)
        {
            return Fail(CollectionsOutcome.FinanceUnavailable, ex.Message);
        }

        if (account is null)
        {
            return Fail(CollectionsOutcome.CandidateChanged, "The candidate's account is no longer reported by the financial source.");
        }

        if (clock.IsStale(account.AsOfUtc))
        {
            return Fail(CollectionsOutcome.FinanceUnavailable, "The financial source's figures for this account are stale; a stale read never authorizes a send.");
        }

        var today = clock.BusinessDate;
        var rules = clock.Rules;
        var cycleKey = ReminderPolicy.CycleKey(candidate.Type, today, rules);
        if (ReminderPolicy.OpenWindows(today, rules).All(w => w.Type != candidate.Type))
        {
            return Fail(CollectionsOutcome.CandidateChanged, $"The {candidate.Type} window is no longer open.");
        }

        var eligibility = ReminderPolicy.Evaluate(account, candidate.Type, today, rules);
        if (!eligibility.IsEligible)
        {
            return Fail(CollectionsOutcome.CandidateChanged, eligibility.Reason == ReminderPolicy.SettledReason
                ? "The account has been settled; it is suppressed and no reminder is queued."
                : $"The account is no longer eligible ({eligibility.Reason}).");
        }

        var existing = await ExistingChannelsAsync([account], candidate.Type, cycleKey, cancellationToken);
        var replacement = ToCandidateDto(account, candidate.Type, cycleKey, eligibility, existing);
        var fresh = new CollectionsCandidate(account.CrmCustomerId, account.AccountId, candidate.Type, cycleKey, eligibility.Amount,
            eligibility.Currency, eligibility.InstalmentIds, candidate.ExpiresAtUtc);

        if (clock.UtcNow > candidate.ExpiresAtUtc)
        {
            return Fail(CollectionsOutcome.CandidateChanged, "The candidate has expired.", replacement);
        }

        if (!candidate.Matches(fresh))
        {
            return Fail(CollectionsOutcome.CandidateChanged, "The balance or eligibility changed since the candidate was issued.", replacement);
        }

        var noContact = channels.Where(c => !HasContact(account, c)).ToList();
        if (noContact.Count > 0)
        {
            return Fail(CollectionsOutcome.NoEligibleContact, $"The financial source holds no approved destination for: {string.Join(", ", noContact)}.");
        }

        return await QueueForAccountAsync(account, candidate.Type, cycleKey, eligibility, channels, language,
            permissions.IsIntegration ? ReminderTrigger.Integration : ReminderTrigger.User, caller.EmployeeId,
            key, requestHash, existing, replacement, cancellationToken);
    }

    // ------------------------------------------------------------------
    // The designated TigerCS scheduler
    // ------------------------------------------------------------------

    /// <summary>
    /// The TigerCS scheduler's run: every eligible, non-stale account in each
    /// window open today gets one job on the scheduled channels TigerCS
    /// dispatches itself. Does nothing unless TigerCS is the designated
    /// scheduler and Collections has confirmed the rules.
    /// </summary>
    public async Task<ScheduledRunResult> RunScheduledAsync(CancellationToken cancellationToken = default)
    {
        if (!options.IsTigerCsSchedulerActive)
        {
            return new ScheduledRunResult(false, 0, 0, false,
                "The TigerCS scheduler is off: it needs Collections:Enabled, Collections:BusinessRulesConfirmed and Collections:SchedulerOwner = \"TigerCS\".");
        }

        var today = clock.BusinessDate;
        var rules = clock.Rules;
        var windows = ReminderPolicy.OpenWindows(today, rules);
        var scheduled = options.Channels.ScheduledChannels.Distinct()
            .Where(c => c != ReminderChannel.VoiceBot && options.Channels.IsEnabled(c)).ToList();
        if (windows.Count == 0 || scheduled.Count == 0)
        {
            return new ScheduledRunResult(true, 0, 0, false, windows.Count == 0 ? "No reminder window is open today." : "No scheduled channel is enabled.");
        }

        var (accounts, truncated) = await ScanAsync(cancellationToken);
        int queued = 0, skipped = 0;
        foreach (var window in windows)
        {
            var offers = accounts
                .Where(a => !clock.IsStale(a.AsOfUtc))
                .Select(a => (Account: a, Eligibility: ReminderPolicy.Evaluate(a, window.Type, today, rules)))
                .Where(o => o.Eligibility.IsEligible)
                .ToList();
            var existing = await ExistingChannelsAsync(offers.Select(o => o.Account), window.Type, window.CycleKey, cancellationToken);

            foreach (var (account, eligibility) in offers)
            {
                var available = AvailableChannels(account, window.Type, window.CycleKey, existing)
                    .Where(scheduled.Contains)
                    .Where(c => !existing.ContainsKey(CollectionsReminder.BuildDeduplicationKey(account.AccountId, window.Type, window.CycleKey, c)))
                    .ToList();
                if (available.Count == 0)
                {
                    skipped++;
                    continue;
                }

                var result = await QueueForAccountAsync(account, window.Type, window.CycleKey, eligibility, available, "en",
                    ReminderTrigger.Scheduled, null, null, null, existing, null, cancellationToken);
                if (result.Outcome == CollectionsOutcome.Accepted)
                {
                    queued++;
                }
                else
                {
                    skipped++;
                }
            }
        }

        return new ScheduledRunResult(true, queued, skipped, truncated, null);
    }

    // ------------------------------------------------------------------

    private async Task<CollectionsResult<CollectionsReminderJobDto>> QueueForAccountAsync(
        FinancialAccountSnapshot account, ReminderType type, string cycleKey, ReminderEligibility eligibility,
        IReadOnlyList<ReminderChannel> channels, string language, ReminderTrigger trigger, Guid? actor,
        string? idempotencyKey, string? requestHash, IReadOnlyDictionary<string, CollectionsReminderChannel> existing,
        CollectionsReminderCandidateDto? replacement, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var used = channels
            .Select(c => existing.GetValueOrDefault(CollectionsReminder.BuildDeduplicationKey(account.AccountId, type, cycleKey, c)))
            .ToList();

        CollectionsReminder job;
        IEnumerable<CollectionsReminderChannel> toDispatch;
        if (used.All(u => u is null))
        {
            job = new CollectionsReminder(account.CrmCustomerId, account.AccountId, account.UnitId, type, cycleKey, eligibility,
                account.AsOfUtc, language, trigger, actor, idempotencyKey, requestHash, channels.ToList(), now);
            await reminderRepository.AddAsync(job, cancellationToken);
            toDispatch = job.Channels;
        }
        else if (used.All(u => u is not null && u.CanRetry && u.Attempts < options.MaxDeliveryAttempts)
                 && used.Select(u => u!.CollectionsReminderId).Distinct().Count() == 1
                 && await reminderRepository.GetByIdAsync(used[0]!.CollectionsReminderId, cancellationToken) is { } retryJob)
        {
            // Retries affect failed channels only, on the job that first sent them.
            job = retryJob;
            var retried = job.Channels.Where(c => channels.Contains(c.Channel)).ToList();
            retried.ForEach(c => c.Requeue(now, actor));
            toDispatch = retried;
        }
        else
        {
            return Fail(CollectionsOutcome.CandidateChanged,
                "One or more requested channels were already used for this account in this cycle.", replacement);
        }

        var correlationId = Guid.NewGuid();
        foreach (var channel in toDispatch)
        {
            if (channel.Channel != ReminderChannel.VoiceBot)
            {
                // Same transaction as the job: a queued SMS/email cannot be lost
                // between "queued" and "sent".
                await outboxWriter.WriteAsync(DispatchEventType,
                    JsonSerializer.Serialize(new DispatchPayload(channel.DeduplicationKey, channel.Attempts)),
                    correlationId, $"collections-dispatch:{channel.DeduplicationKey}:{channel.Attempts}", now, cancellationToken);
            }

            await auditWriter.WriteAsync(actor, "CollectionsReminderQueued", AuditEntityType,
                CollectionsHashing.AuditEntityId(channel.DeduplicationKey), null,
                $"Amount={job.Amount} {job.Currency};Basis={job.AmountBasis};Attempt={channel.Attempts};Trigger={trigger};SourceAsOfUtc={account.AsOfUtc:O}",
                correlationId, cancellationToken);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateWriteException)
        {
            unitOfWork.DiscardPendingChanges();
            if (idempotencyKey is not null && await reminderRepository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken) is { } winner)
            {
                return winner.RequestHash == requestHash
                    ? CollectionsResult<CollectionsReminderJobDto>.Ok(CollectionsMapper.ToJobDto(winner), CollectionsOutcome.Replayed)
                    : Fail(CollectionsOutcome.IdempotencyConflict, "This Idempotency-Key was already used with a different request.");
            }

            logger.LogInformation("Concurrent reminder for {AccountId} {Type} {CycleKey} lost the de-duplication race.", account.AccountId, type, cycleKey);
            return Fail(CollectionsOutcome.CandidateChanged, "A concurrent request already queued this reminder for the cycle.", replacement);
        }

        return CollectionsResult<CollectionsReminderJobDto>.Ok(CollectionsMapper.ToJobDto(job), CollectionsOutcome.Accepted);
    }

    private CollectionsReminderCandidateDto ToCandidateDto(
        FinancialAccountSnapshot account, ReminderType type, string cycleKey, ReminderEligibility eligibility,
        IReadOnlyDictionary<string, CollectionsReminderChannel> existing)
    {
        var expires = clock.UtcNow.AddMinutes(Math.Max(1, options.CandidateValidityMinutes));
        var token = new CollectionsCandidate(account.CrmCustomerId, account.AccountId, type, cycleKey, eligibility.Amount,
            eligibility.Currency, eligibility.InstalmentIds, expires);
        return new CollectionsReminderCandidateDto(
            token.Encode(), account.CrmCustomerId, account.AccountId, account.UnitId, account.TowerName, account.UnitNumber,
            eligibility.Currency, CollectionsMoney.Two(eligibility.Amount), eligibility.AmountBasis, eligibility.InstalmentIds, eligibility.OldestUnpaidDueDate,
            AvailableChannels(account, type, cycleKey, existing).Select(c => c.ToString()).ToList(),
            account.AsOfUtc, expires);
    }

    private List<ReminderChannel> AvailableChannels(
        FinancialAccountSnapshot account, ReminderType type, string cycleKey, IReadOnlyDictionary<string, CollectionsReminderChannel> existing) =>
        Enum.GetValues<ReminderChannel>()
            .Where(options.Channels.IsEnabled)
            .Where(c => HasContact(account, c))
            .Where(c => existing.GetValueOrDefault(CollectionsReminder.BuildDeduplicationKey(account.AccountId, type, cycleKey, c)) is not { } used
                || (used.CanRetry && used.Attempts < options.MaxDeliveryAttempts))
            .ToList();

    private static bool HasContact(FinancialAccountSnapshot account, ReminderChannel channel) => channel switch
    {
        ReminderChannel.Email => !string.IsNullOrWhiteSpace(account.CustomerEmail),
        _ => !string.IsNullOrWhiteSpace(account.CustomerPhone)
    };

    private async Task<IReadOnlyDictionary<string, CollectionsReminderChannel>> ExistingChannelsAsync(
        IEnumerable<FinancialAccountSnapshot> accounts, ReminderType type, string cycleKey, CancellationToken cancellationToken)
    {
        var keys = accounts
            .SelectMany(a => Enum.GetValues<ReminderChannel>().Select(c => CollectionsReminder.BuildDeduplicationKey(a.AccountId, type, cycleKey, c)))
            .ToList();
        return keys.Count == 0
            ? new Dictionary<string, CollectionsReminderChannel>()
            : (await reminderRepository.GetChannelsByDeduplicationKeysAsync(keys, cancellationToken)).ToDictionary(c => c.DeduplicationKey);
    }

    private async Task<IReadOnlyList<FinancialAccountSnapshot>> ScopeAsync(long? crmCustomerId, string? accountId, CancellationToken cancellationToken)
    {
        if (crmCustomerId is { } customer)
        {
            var accounts = await source.GetCustomerAccountsAsync(customer, cancellationToken) ?? [];
            return accounts.Where(a => a.CrmCustomerId == customer
                && (string.IsNullOrWhiteSpace(accountId) || a.AccountId == accountId.Trim())).ToList();
        }

        return (await ScanAsync(cancellationToken)).Accounts;
    }

    private async Task<(IReadOnlyList<FinancialAccountSnapshot> Accounts, bool Truncated)> ScanAsync(CancellationToken cancellationToken)
    {
        const int sourcePageSize = 200;
        var all = new List<FinancialAccountSnapshot>();
        for (var page = 1; ; page++)
        {
            var batch = await source.ListAccountsWithOutstandingPrincipalAsync(page, sourcePageSize, cancellationToken);
            all.AddRange(batch.Accounts);
            if (!batch.HasMore || batch.Accounts.Count == 0)
            {
                return (all, false);
            }

            if (all.Count >= options.MaxAccountsPerScan)
            {
                logger.LogWarning("Collections candidate scan stopped at {Max} accounts; more remain in the source.", options.MaxAccountsPerScan);
                return (all, true);
            }
        }
    }

    private async Task<CollectionsResult<T>?> SendGateAsync<T>(CollectionsCaller caller, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return CollectionsResult<T>.Fail(CollectionsOutcome.Disabled);
        }

        return (await authorization.ResolveAsync(caller, cancellationToken)).CanSendReminders
            ? null
            : CollectionsResult<T>.Fail(CollectionsOutcome.Forbidden, "Reminder candidates and queueing require the Collections reminder permission.");
    }

    private static CollectionsResult<CollectionsReminderJobDto> Fail(
        CollectionsOutcome outcome, string? detail = null, CollectionsReminderCandidateDto? replacement = null) =>
        CollectionsResult<CollectionsReminderJobDto>.Fail(outcome, detail, replacement);

    internal sealed record DispatchPayload(string DeduplicationKey, int Attempt);
}

public sealed record ScheduledRunResult(bool Ran, int Queued, int Skipped, bool Truncated, string? Detail);
