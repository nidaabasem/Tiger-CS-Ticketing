using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Collections;

/// <summary>
/// The "Collections" configuration section — a plain value bound by the
/// composition root, exactly as <c>GenesysOptions</c> is.
///
/// <para>
/// <b>Everything that sends is off by default.</b> <see cref="Enabled"/>
/// gates the whole API; each channel is off until switched on; automatic
/// scheduling additionally requires <see cref="BusinessRulesConfirmed"/>,
/// which stays false until Collections confirms the four open FAQ points
/// (calendar months vs fixed days, fines in the amount, once per window vs
/// daily, month-end and February handling).
/// </para>
/// </summary>
public sealed class CollectionsOptions
{
    public const string SectionName = "Collections";

    /// <summary>Feature flag for every Collections endpoint. False answers 503 and reads nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>The TigerCS department whose Department Employees/Heads hold Collections permissions. Matched by code, never by name.</summary>
    public string CollectionsDepartmentCode { get; set; } = "COL";

    /// <summary>The business time zone that decides "today", "overdue" and the month windows.</summary>
    public string TimeZoneId { get; set; } = "Asia/Dubai";

    /// <summary>Source figures older than this are flagged stale in every response.</summary>
    public int StaleAfterMinutes { get; set; } = 60;

    /// <summary>Upper bound on accounts read from the source per candidates/scheduler scan.</summary>
    public int MaxAccountsPerScan { get; set; } = 5000;

    public CollectionsAuthorizationOptions Authorization { get; set; } = new();

    public CollectionsResponseTicketOptions ResponseTickets { get; set; } = new();

    public CollectionsChannelOptions Channels { get; set; } = new();

    public CollectionsReminderRulesOptions Rules { get; set; } = new();

    /// <summary>Registers the designated recurring job. Ignored unless <see cref="BusinessRulesConfirmed"/> is also true.</summary>
    public bool AutomaticSchedulingEnabled { get; set; }

    /// <summary>Collections has confirmed the four open rule decisions in <see cref="Rules"/>.</summary>
    public bool BusinessRulesConfirmed { get; set; }

    /// <summary>Cron for the daily scheduler run, in <see cref="TimeZoneId"/>.</summary>
    public string ScheduleCron { get; set; } = "0 9 * * *";

    public bool IsAutomaticSchedulingActive => Enabled && AutomaticSchedulingEnabled && BusinessRulesConfirmed;
}

/// <summary>Explicit financial authorization. Role lists are configuration so Collections can tighten them without a release.</summary>
public sealed class CollectionsAuthorizationOptions
{
    /// <summary>Roles that may read balances, instalments, payments and reminder history for any customer.</summary>
    public List<string> FinancialReadRoles { get; set; } =
        [Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo];

    /// <summary>Roles that may send a reminder and list reminder candidates.</summary>
    public List<string> ReminderSendRoles { get; set; } = [Roles.CsSupervisor, Roles.CsManager];

    /// <summary>
    /// Department roles that hold BOTH permissions, but only for members of
    /// the Collections department (<see cref="CollectionsOptions.CollectionsDepartmentCode"/>).
    /// </summary>
    public List<string> CollectionsDepartmentRoles { get; set; } = [Roles.DepartmentEmployee, Roles.DepartmentHead];

    /// <summary>
    /// TigerCS employee ids of integration service accounts (the TigerGroupWeb
    /// account Genesys calls through). Only these may report reminder
    /// outcomes; they may also read, list candidates and record reminders.
    /// </summary>
    public List<Guid> IntegrationEmployeeIds { get; set; } = [];
}

/// <summary>
/// Where a reminder response's ticket is routed: a department code or a
/// Genesys queue id, resolved by the existing Genesys ingestion exactly as
/// for any other conversation. Neither set means responses are recorded and
/// their ticket stays pending (retried) until it is configured.
/// </summary>
public sealed class CollectionsResponseTicketOptions
{
    public string? DepartmentCode { get; set; } = "COL";

    public string? QueueId { get; set; }
}

public sealed class CollectionsChannelOptions
{
    /// <summary>Genesys outbound voice bot. Genesys places the call and reports outcomes; TigerCS never dials.</summary>
    public bool VoiceBotEnabled { get; set; }

    /// <summary>No approved SMS provider exists in TigerCS; enabling this without one fails closed at dispatch.</summary>
    public bool SmsEnabled { get; set; }

    /// <summary>Email through the existing EmailNotifications sender (SMTP, or Recording in tests).</summary>
    public bool EmailEnabled { get; set; }

    /// <summary>Channels the automatic scheduler uses.</summary>
    public List<ReminderChannel> ScheduledChannels { get; set; } = [ReminderChannel.VoiceBot];

    public bool IsEnabled(ReminderChannel channel) => channel switch
    {
        ReminderChannel.VoiceBot => VoiceBotEnabled,
        ReminderChannel.Sms => SmsEnabled,
        ReminderChannel.Email => EmailEnabled,
        _ => false
    };
}

/// <summary>Bindable form of <see cref="ReminderRuleSettings"/>. Every default is the conservative, unconfirmed reading.</summary>
public sealed class CollectionsReminderRulesOptions
{
    public OverdueAgeRule OverdueAgeRule { get; set; } = OverdueAgeRule.CalendarMonth;
    public int OverdueFixedDays { get; set; } = 30;
    public int OverdueWindowFirstDay { get; set; } = 1;
    public int OverdueWindowLastDay { get; set; } = 4;
    public int CurrentMonthDueDay { get; set; } = 15;
    public int MonthEndOffsetDays { get; set; } = 3;
    public bool IncludeFinesInReminderAmount { get; set; }
    public ReminderSendFrequency SendFrequency { get; set; } = ReminderSendFrequency.OncePerWindow;

    public ReminderRuleSettings ToSettings() => new()
    {
        OverdueAgeRule = OverdueAgeRule,
        OverdueFixedDays = Math.Clamp(OverdueFixedDays, 1, 366),
        OverdueWindowFirstDay = Math.Clamp(OverdueWindowFirstDay, 1, 28),
        OverdueWindowLastDay = Math.Clamp(OverdueWindowLastDay, 1, 28),
        CurrentMonthDueDay = Math.Clamp(CurrentMonthDueDay, 1, 28),
        MonthEndOffsetDays = Math.Clamp(MonthEndOffsetDays, 0, 20),
        IncludeFinesInReminderAmount = IncludeFinesInReminderAmount,
        SendFrequency = SendFrequency
    };
}
