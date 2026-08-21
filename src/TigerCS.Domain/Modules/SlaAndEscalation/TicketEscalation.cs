using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md §2.17 / MVP-ERD.md §2.17 — one row per escalation
/// event, manual or automatic. Never deleted.
///
/// <para>
/// <b><see cref="NotifiedRoles"/> is deliberately distinct from
/// <see cref="Level"/></b> — ADR-0011's central point, and the reason this
/// row exists rather than the ticket carrying a level alone. "The GM has
/// been told, but the Department Head is still the active owner" is a real
/// intermediate state a single signal cannot express, so a Critical breach
/// notifying the GM does not, by itself, make the ticket Level 3
/// (SLA-Architecture.md §11).
/// </para>
///
/// <para>
/// <b>No <c>RaisedByEmployeeId</c> column</b>, because MVP-Data-Dictionary.md
/// §2.17's approved column set has none. The acting employee for a manual
/// escalation is recorded in <c>AuditEntries.ActorEmployeeId</c>, which is
/// what ADR-0018's append-only audit trail is for; adding a second, parallel
/// actor column here would duplicate it and put the approved schema and the
/// implementation out of step.
/// </para>
/// </summary>
public class TicketEscalation
{
    public long TicketEscalationId { get; private set; }
    public long TicketId { get; private set; }
    public byte Level { get; private set; }
    public EscalationTriggerType TriggerType { get; private set; }

    /// <summary>Who the approved recipient matrix says should be told (SLA-Architecture.md §11, Solution-Analysis.md §7.6/§7.8) — recorded here even though notification <i>delivery</i> is out of this increment's scope, because the split from <see cref="Level"/> is the schema's point.</summary>
    public string? NotifiedRoles { get; private set; }

    public DateTime RaisedAtUtc { get; private set; }

    /// <summary>Set by MVP-API-Contracts.md §5.8's respond action, which does not ship in this increment — see the PR description's reported contract gap. Present because it is part of §2.17's approved column set.</summary>
    public DateTime? RespondedAtUtc { get; private set; }

    /// <summary>See <see cref="RespondedAtUtc"/>.</summary>
    public Guid? RespondingEmployeeId { get; private set; }

    private TicketEscalation() { }

    private TicketEscalation(long ticketId, byte level, EscalationTriggerType triggerType, string? notifiedRoles, DateTime raisedAtUtc)
    {
        TicketId = ticketId;
        Level = level;
        TriggerType = triggerType;
        NotifiedRoles = notifiedRoles;
        RaisedAtUtc = raisedAtUtc;
    }

    /// <summary>
    /// FR-ESC-02 / backlog S-11 — the automatic Level 2 (Department Head)
    /// escalation raised the moment an SLA deadline is detected breached.
    ///
    /// <para>
    /// Always Level 2. The timed Level 2 → Level 3 advance is a separate,
    /// scheduled-job feature that MVP-Implementation-Backlog.md §0.2 and
    /// S-11 both place outside this pilot, so nothing here can produce a
    /// Level 3 row; Level 3 and Level 4 are manual-only in this build.
    /// </para>
    /// </summary>
    public static TicketEscalation RaiseAutomaticLevel2OnBreach(long ticketId, byte priorityId, DateTime raisedAtUtc) =>
        new(ticketId, (byte)Ticketing.EscalationLevel.Level2, EscalationTriggerType.AutoBreach, BreachNotifiedRolesFor(priorityId), raisedAtUtc);

    /// <summary>MVP-API-Contracts.md §5.7 — a manual escalation. Role gating for <see cref="EscalationTriggerType.ManualLevel4"/> is an authorization concern and is enforced before this is reached.</summary>
    public static TicketEscalation RaiseManual(long ticketId, byte level, EscalationTriggerType triggerType, DateTime raisedAtUtc)
    {
        if (triggerType is not (EscalationTriggerType.ManualFlag or EscalationTriggerType.ManualLevel4))
        {
            throw new ArgumentException(
                "A manual escalation must carry TriggerType ManualFlag or ManualLevel4.", nameof(triggerType));
        }

        return new TicketEscalation(ticketId, level, triggerType, NotifiedRolesForLevel(level), raisedAtUtc);
    }

    /// <summary>
    /// SLA-Architecture.md §11's approved breach-recipient matrix (identical
    /// to Solution-Analysis.md §7.8's): Critical and High notify the
    /// Department Head and the GM together (ISSUE-004), Medium the Department
    /// Head, Low the Supervisor.
    /// </summary>
    private static string BreachNotifiedRolesFor(byte priorityId) => (PriorityLevel)priorityId switch
    {
        PriorityLevel.Critical or PriorityLevel.High => $"{Roles.DepartmentHead},{Roles.GeneralManager}",
        PriorityLevel.Medium => Roles.DepartmentHead,
        PriorityLevel.Low => Roles.CsSupervisor,
        _ => Roles.DepartmentHead
    };

    /// <summary>Solution-Analysis.md §7.6's escalation hierarchy: Level 1 the agent's own attempt, Level 2 the Department Head, Level 3 the GM, Level 4 the Chairman/CEO.</summary>
    private static string? NotifiedRolesForLevel(byte level) => (Ticketing.EscalationLevel)level switch
    {
        Ticketing.EscalationLevel.Level1 => Roles.CsSupervisor,
        Ticketing.EscalationLevel.Level2 => Roles.DepartmentHead,
        Ticketing.EscalationLevel.Level3 => Roles.GeneralManager,
        Ticketing.EscalationLevel.Level4 => Roles.ChairmanCeo,
        _ => null
    };
}
