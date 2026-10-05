using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Collections;

/// <summary>
/// The "Collections" configuration section — a plain value bound by the
/// composition root, exactly as <c>GenesysOptions</c> is.
///
/// <para>
/// <b>Everything that sends is off by default.</b> <see cref="Enabled"/>
/// gates every route; each channel is off until switched on; automatic
/// scheduling additionally needs <see cref="BusinessRulesConfirmed"/> and a
/// <see cref="SchedulerOwner"/> of <c>TigerCS</c>.
/// </para>
/// </summary>
public sealed class CollectionsOptions
{
    public const string SectionName = "Collections";

    /// <summary>Feature flag for every Collections route. False answers 503 and reads nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>The TigerCS department whose Department Employees/Heads hold Collections permissions. Matched by code, never by name.</summary>
    public string CollectionsDepartmentCode { get; set; } = "COL";

    /// <summary>The business calendar for businessDate, overdue and the reminder windows.</summary>
    public string TimeZoneId { get; set; } = "Asia/Dubai";

    /// <summary>Source figures older than this are <c>dataStatus: "Stale"</c>, and a stale read never authorizes a send.</summary>
    public int StaleAfterMinutes { get; set; } = 60;

    /// <summary>How long a reminder candidate may be queued after it was issued (proposed: 15 minutes).</summary>
    public int CandidateValidityMinutes { get; set; } = 15;

    /// <summary>Delivery attempts per channel per cycle, the first included. Retries affect failed channels only.</summary>
    public int MaxDeliveryAttempts { get; set; } = 3;

    /// <summary>Upper bound on accounts read from the source per candidates/scheduler scan.</summary>
    public int MaxAccountsPerScan { get; set; } = 5000;

    public CollectionsAuthorizationOptions Authorization { get; set; } = new();

    public CollectionsResponseTicketOptions ResponseTickets { get; set; } = new();

    public CollectionsChannelOptions Channels { get; set; } = new();

    public CollectionsReminderRulesOptions Rules { get; set; } = new();

    /// <summary>
    /// Who owns the reminder cycle: "None" (default — nobody schedules),
    /// "TigerCS" (the Hangfire job queues SMS/email reminders) or "Genesys"
    /// (the outbound campaign pulls candidates and queues). One designated
    /// scheduler: the TigerCS job is registered only for "TigerCS".
    /// </summary>
    public string SchedulerOwner { get; set; } = "None";

    /// <summary>Collections has confirmed the open rule decisions in <see cref="Rules"/>.</summary>
    public bool BusinessRulesConfirmed { get; set; }

    /// <summary>Cron for the TigerCS scheduler's daily run, in <see cref="TimeZoneId"/>.</summary>
    public string ScheduleCron { get; set; } = "0 9 * * *";

    public bool IsTigerCsSchedulerActive =>
        Enabled && BusinessRulesConfirmed && string.Equals(SchedulerOwner, "TigerCS", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Explicit financial authorization — separate grants for viewing payments,
/// queueing reminders and reporting delivery. Role lists are configuration so
/// Collections can tighten them without a release.
/// </summary>
public sealed class CollectionsAuthorizationOptions
{
    /// <summary>Roles that may read balances, instalments, payments and reminder history.</summary>
    public List<string> FinancialReadRoles { get; set; } =
        [Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo];

    /// <summary>Roles that may list candidates and queue reminders.</summary>
    public List<string> ReminderSendRoles { get; set; } = [Roles.CsSupervisor, Roles.CsManager];

    /// <summary>Department roles that hold both grants, but only for members of the Collections department.</summary>
    public List<string> CollectionsDepartmentRoles { get; set; } = [Roles.DepartmentEmployee, Roles.DepartmentHead];

    /// <summary>TigerCS employee ids of integration service accounts (the TigerGroupWeb account Genesys calls through). Only these report outcomes or queue VoiceBot reminders.</summary>
    public List<Guid> IntegrationEmployeeIds { get; set; } = [];
}

/// <summary>Where a reminder response's ticket is routed — a Genesys queue id (via the queue mapping) or a department code. Never a guessed department id.</summary>
public sealed class CollectionsResponseTicketOptions
{
    public string? DepartmentCode { get; set; } = "COL";

    public string? QueueId { get; set; }
}

public sealed class CollectionsChannelOptions
{
    /// <summary>Genesys outbound voice bot. Genesys dials and reports outcomes; TigerCS never dials.</summary>
    public bool VoiceBotEnabled { get; set; }

    /// <summary>No approved SMS provider exists in TigerCS; enabling this fails closed at dispatch.</summary>
    public bool SmsEnabled { get; set; }

    /// <summary>Email through the existing EmailNotifications sender.</summary>
    public bool EmailEnabled { get; set; }

    /// <summary>Channels the TigerCS scheduler queues (VoiceBot is never TigerCS-scheduled).</summary>
    public List<ReminderChannel> ScheduledChannels { get; set; } = [ReminderChannel.Sms, ReminderChannel.Email];

    public bool IsEnabled(ReminderChannel channel) => channel switch
    {
        ReminderChannel.VoiceBot => VoiceBotEnabled,
        ReminderChannel.Sms => SmsEnabled,
        ReminderChannel.Email => EmailEnabled,
        _ => false
    };
}

/// <summary>Bindable form of <see cref="ReminderRuleSettings"/>. Defaults are the specification's proposed drafts, not confirmed rules.</summary>
public sealed class CollectionsReminderRulesOptions
{
    public OverdueAgeRule OverdueAgeRule { get; set; } = OverdueAgeRule.CalendarMonth;
    public int OverdueFixedDays { get; set; } = 30;
    public int OverdueWindowFirstDay { get; set; } = 1;
    public int OverdueWindowLastDay { get; set; } = 4;
    public int CurrentMonthDay { get; set; } = 15;
    public int MonthEndOffsetDays { get; set; } = 3;
    public bool IncludePenaltiesAndFees { get; set; }
    public ReminderSendFrequency SendFrequency { get; set; } = ReminderSendFrequency.OncePerWindow;

    public ReminderRuleSettings ToSettings() => new()
    {
        OverdueAgeRule = OverdueAgeRule,
        OverdueFixedDays = Math.Clamp(OverdueFixedDays, 1, 366),
        OverdueWindowFirstDay = Math.Clamp(OverdueWindowFirstDay, 1, 28),
        OverdueWindowLastDay = Math.Clamp(OverdueWindowLastDay, 1, 28),
        CurrentMonthDay = Math.Clamp(CurrentMonthDay, 1, 28),
        MonthEndOffsetDays = Math.Clamp(MonthEndOffsetDays, 0, 20),
        IncludePenaltiesAndFees = IncludePenaltiesAndFees,
        SendFrequency = SendFrequency
    };
}
