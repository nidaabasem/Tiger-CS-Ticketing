using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// An OTP-challenge store that behaves like the database for what matters
/// here: GetById hands out the <i>tracked</i> instance (changes are visible
/// until saved), SaveChanges commits tracked changes, and
/// <c>DiscardPendingChanges</c> throws the tracked copy away so the next read
/// is the committed state — which is exactly what a concurrency-token
/// conflict relies on.
/// </summary>
public sealed class FakeOtpStore : ICustomerOtpChallengeRepository
{
    private readonly Dictionary<Guid, CustomerOtpChallenge> _committed = [];
    private readonly Dictionary<Guid, CustomerOtpChallenge> _tracked = [];

    public IReadOnlyCollection<CustomerOtpChallenge> Committed => _committed.Values;

    public CustomerOtpChallenge Single() => _committed.Values.Single();

    private static CustomerOtpChallenge Clone(CustomerOtpChallenge c) =>
        (CustomerOtpChallenge)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(c, null)!;

    public Task<CustomerOtpChallenge?> GetByIdAsync(Guid challengeId, CancellationToken cancellationToken = default)
    {
        if (_tracked.TryGetValue(challengeId, out var tracked))
        {
            return Task.FromResult<CustomerOtpChallenge?>(tracked);
        }

        if (!_committed.TryGetValue(challengeId, out var committed))
        {
            return Task.FromResult<CustomerOtpChallenge?>(null);
        }

        return Task.FromResult<CustomerOtpChallenge?>(_tracked[challengeId] = Clone(committed));
    }

    public Task<CustomerOtpChallenge?> FindPendingAsync(
        Guid callerEmployeeId, int crmCustomerId, int crmLeadId, OtpChannel channel, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(_committed.Values
            .Where(c => c.CallerEmployeeId == callerEmployeeId && c.CrmCustomerId == crmCustomerId && c.CrmLeadId == crmLeadId && c.Channel == channel
                && c.Status == OtpChallengeStatus.Pending && c.ExpiresAtUtc >= nowUtc)
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => GetByIdAsync(c.CustomerOtpChallengeId).Result)
            .FirstOrDefault());

    public Task<int> CountStartedSinceAsync(int crmCustomerId, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(_committed.Values.Count(c => c.CrmCustomerId == crmCustomerId && c.CreatedAtUtc >= sinceUtc));

    public Task AddAsync(CustomerOtpChallenge challenge, CancellationToken cancellationToken = default)
    {
        _tracked[challenge.CustomerOtpChallengeId] = challenge;
        return Task.CompletedTask;
    }

    public void Commit()
    {
        foreach (var (id, challenge) in _tracked)
        {
            _committed[id] = Clone(challenge);
        }
    }

    public void Discard() => _tracked.Clear();

    /// <summary>Another request's write, applied straight to the committed state.</summary>
    public void CommitFromAnotherRequest(Guid id, Action<CustomerOtpChallenge> change)
    {
        change(_committed[id]);
        _tracked.Remove(id);
    }
}

/// <summary>Commits the OTP store on save, and can simulate losing a concurrency race exactly once.</summary>
public sealed class OtpUnitOfWork(FakeOtpStore store) : ICustomerVerificationUnitOfWork
{
    private Action? _losesNextSave;

    public int SaveCount { get; private set; }

    /// <summary>The next save fails with a concurrency conflict after <paramref name="otherRequestWins"/> has applied the other request's write.</summary>
    public void LoseNextSave(Action otherRequestWins) => _losesNextSave = otherRequestWins;

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        if (_losesNextSave is { } wins)
        {
            _losesNextSave = null;
            wins();
            throw new ConcurrentWriteException(new InvalidOperationException("Simulated concurrency-token conflict."));
        }

        store.Commit();
        return Task.CompletedTask;
    }

    public void DiscardPendingChanges() => store.Discard();
}

public sealed class FixedCodes(params string[] codes) : IOtpCodeGenerator
{
    private readonly Queue<string> _codes = new(codes);
    public List<string> Issued { get; } = [];

    public string NewCode()
    {
        var code = _codes.Count > 0 ? _codes.Dequeue() : "999999";
        Issued.Add(code);
        return code;
    }
}

public sealed class OtpServiceFixture
{
    public static readonly Guid Caller = Guid.NewGuid();
    public const string Phone = "+971501234567";

    public CrmDocumentOptions Options { get; } = new() { Enabled = true, OtpCodePepper = "test-pepper" };
    public FakeCrmBuyerLookupGateway Buyers { get; } = new();
    public FakeUnitReferenceRepository Units { get; } = new();
    public FakeContactReferenceRepository Contacts { get; } = new();
    public FakeVerificationSessionRepository Sessions { get; } = new();
    public FakeOtpStore Store { get; } = new();
    public OtpUnitOfWork UnitOfWork { get; }
    public FakeEmailSender Email { get; } = new();
    public TigerCS.Integrations.Modules.SmsIntegration.FakeSmsSender Sms { get; } = new(
        Microsoft.Extensions.Options.Options.Create(new TigerCS.Integrations.Modules.SmsIntegration.SmsOptions()),
        NullLogger<TigerCS.Integrations.Modules.SmsIntegration.FakeSmsSender>.Instance);
    public FixedCodes Codes { get; } = new("111111", "222222", "333333", "444444", "555555", "666666");
    public FakeAuditEntryWriter Audit { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc));
    public CustomerOtpAppService Service { get; private set; }
    public CustomerNotificationPolicy EmailPolicy { get; set; } = CustomerNotificationPolicy.EnabledDefault;

    public OtpServiceFixture(
        string? email = "ahmed.ali@example.com", string? mobile = Phone, bool twoUnits = true, CrmBuyerLookupResult? lookup = null)
    {
        UnitOfWork = new OtpUnitOfWork(Store);

        var units = new List<CrmBuyerUnitDto>
        {
            Unit(12345, 1101, "1205", "Tiger Sky Tower")
        };
        if (twoUnits)
        {
            units.Add(Unit(12346, 1102, "1403", "Tiger Sky Tower"));
        }

        Buyers.Returns(lookup ?? CrmBuyerLookupResult.Success(
            [new CrmBuyerMatchDto(new CrmCustomerDto(9001, "Ahmed Ali", "أحمد", mobile, email), units)]));

        Service = Build();
    }

    public static CrmBuyerUnitDto Unit(int leadId, int unitId, string? number, string? project, int customerType = 1) =>
        new(leadId, 8, "Sold", unitId, number, 3, 2, 12, 79, project, null, customerType, "Buyer");

    /// <summary>Replaces the SMS provider (e.g. with one that reports itself unconfigured) and rebuilds the service.</summary>
    public void UseSmsSender(TigerCS.Application.Modules.Notifications.Abstractions.ISmsSender sender)
    {
        _smsSender = sender;
        Service = Build();
    }

    private TigerCS.Application.Modules.Notifications.Abstractions.ISmsSender? _smsSender;

    private CustomerOtpAppService Build()
    {
        var sessionService = new VerificationSessionAppService(Sessions, Units, Contacts, UnitOfWork, Audit, Clock);
        var cache = new CrmBuyerVerificationCache(Units, Contacts, UnitOfWork, Clock);
        return new CustomerOtpAppService(
            Options, new CrmBuyerLookupAppService(Buyers, NullLogger<CrmBuyerLookupAppService>.Instance), cache, Store, Units, Contacts,
            sessionService, Codes, new ForwardingEmail(this), _smsSender ?? Sms, EmailPolicy, UnitOfWork, Audit, Clock, NullLogger<CustomerOtpAppService>.Instance);
    }

    // The policy is read at construction; rebuild when a test changes it.
    public void UseEmailPolicy(CustomerNotificationPolicy policy)
    {
        EmailPolicy = policy;
        Service = Build();
    }

    private sealed class ForwardingEmail(OtpServiceFixture f) : TigerCS.Application.Modules.Notifications.Abstractions.IEmailSender
    {
        public Task<TigerCS.Application.Modules.Notifications.Abstractions.EmailSendResult> SendAsync(
            TigerCS.Application.Modules.Notifications.Abstractions.EmailMessage message, CancellationToken cancellationToken = default) =>
            f.Email.SendAsync(message, cancellationToken);
    }

    /// <summary>Turns the SMS channel on (CrmDocuments:OtpSmsEnabled) for a test. The fake sender counts as a configured provider.</summary>
    public void EnableSms() => Options.OtpSmsEnabled = true;

    /// <summary>The 6-digit code in the most recent SMS.</summary>
    public string LastSmsCode() => System.Text.RegularExpressions.Regex.Match(Sms.Last!.Text, @"\b\d{6}\b").Value;

    /// <summary>The 6-digit code in the most recent email.</summary>
    public string LastEmailedCode() =>
        System.Text.RegularExpressions.Regex.Match(Email.Sent.Last().Body, @"\b\d{6}\b").Value;
}
