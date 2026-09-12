using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>
/// The Customers directory, computed by the database from what TigerCS has
/// persisted: every visible ticket is attributed to one customer identity —
/// CRM Buyer id, else the external-verification pair, else the intake phone
/// (<see cref="CustomerIdentityKind"/>) — and the identities are grouped,
/// counted, ordered by most recent ticket and paged in SQL. The page's rows
/// are then dressed with what each customer's latest ticket snapshotted
/// (name, project/unit, phone). A ticket with no identity at all is not a
/// customer and is left out; nothing here calls CRM.
/// </summary>
public sealed class CustomerDirectoryRepository(TigerCsDbContext dbContext) : ICustomerDirectoryRepository
{
    private const int CrmKind = (int)CustomerIdentityKind.Crm;
    private const int ExternalKind = (int)CustomerIdentityKind.External;
    private const int PhoneKind = (int)CustomerIdentityKind.Phone;

    /// <summary>One visible ticket with its resolved identity columns — exactly one of the three identity slots is populated, by kind.</summary>
    private sealed class IdentifiedTicket
    {
        public Ticket Ticket { get; init; } = null!;
        public string? IntakePhone { get; init; }
        public int Kind { get; init; }
        public int? CrmId { get; init; }
        public string? Source { get; init; }
        public string? ExternalId { get; init; }
        public string? Phone { get; init; }
        public DateTime? LastInteractionAtUtc { get; init; }
    }

    private IQueryable<IdentifiedTicket> Identified(IReadOnlyCollection<int>? visibleDepartmentIds)
    {
        var tickets = dbContext.Tickets.AsNoTracking().InScope(visibleDepartmentIds);

        var withPhone = tickets.Select(t => new
        {
            Ticket = t,
            IntakePhone = dbContext.IntakeRecords
                .Where(i => i.LinkedTicketId == t.TicketId && i.PhoneNumber != "")
                .OrderBy(i => i.IntakeRecordId)
                .Select(i => (string?)i.PhoneNumber)
                .FirstOrDefault(),
            LastInteractionAtUtc = dbContext.TicketInteractions
                .Where(i => i.TicketId == t.TicketId)
                .Max(i => (DateTime?)i.CreatedAtUtc),
        });

        return withPhone.Select(x => new IdentifiedTicket
        {
            Ticket = x.Ticket,
            IntakePhone = x.IntakePhone,
            LastInteractionAtUtc = x.LastInteractionAtUtc,
            Kind = x.Ticket.CrmBuyerCustomerId != null ? CrmKind
                : x.Ticket.CustomerVerificationSource != null && x.Ticket.ExternalCustomerId != null ? ExternalKind
                : x.IntakePhone != null ? PhoneKind
                : 0,
            CrmId = x.Ticket.CrmBuyerCustomerId,
            Source = x.Ticket.CrmBuyerCustomerId == null && x.Ticket.ExternalCustomerId != null ? x.Ticket.CustomerVerificationSource : null,
            ExternalId = x.Ticket.CrmBuyerCustomerId == null && x.Ticket.CustomerVerificationSource != null ? x.Ticket.ExternalCustomerId : null,
            Phone = x.Ticket.CrmBuyerCustomerId == null && (x.Ticket.CustomerVerificationSource == null || x.Ticket.ExternalCustomerId == null) ? x.IntakePhone : null,
        }).Where(x => x.Kind != 0);
    }

    public async Task<CustomerDirectoryPage> ListAsync(CustomerDirectoryQuery query, CancellationToken cancellationToken = default)
    {
        var identified = Identified(query.VisibleDepartmentIds);
        var candidates = identified;

        // Search and the department filter select CUSTOMERS (any of whose
        // tickets match), never individual tickets — the counts on a row are
        // always the customer's whole footprint. Expressed as a correlated
        // EXISTS over the same identity columns.
        if (query.Search is { } search)
        {
            var matching = identified.Where(m =>
                m.Ticket.TicketNumber.Contains(search)
                || (m.Ticket.CrmBuyerCustomerName != null && m.Ticket.CrmBuyerCustomerName.Contains(search))
                || (m.Ticket.CrmBuyerUnitNumber != null && m.Ticket.CrmBuyerUnitNumber.Contains(search))
                || (m.Ticket.CrmBuyerProjectName != null && m.Ticket.CrmBuyerProjectName.Contains(search))
                || (m.Ticket.ManualUnitNumber != null && m.Ticket.ManualUnitNumber.Contains(search))
                || (m.Ticket.ManualProjectName != null && m.Ticket.ManualProjectName.Contains(search))
                || (m.Ticket.ExternalCustomerId != null && m.Ticket.ExternalCustomerId.Contains(search))
                || (m.IntakePhone != null && m.IntakePhone.Contains(search))
                || dbContext.TicketInteractions.Any(i => i.TicketId == m.Ticket.TicketId
                    && ((i.CustomerName != null && i.CustomerName.Contains(search)) || i.CustomerPhone.Contains(search))));
            candidates = candidates.Where(x => matching.Any(m =>
                m.Kind == x.Kind
                && ((x.Kind == CrmKind && m.CrmId == x.CrmId)
                    || (x.Kind == ExternalKind && m.Source == x.Source && m.ExternalId == x.ExternalId)
                    || (x.Kind == PhoneKind && m.Phone == x.Phone))));
        }

        if (query.DepartmentId is { } departmentId)
        {
            var inDepartment = identified.Where(m => m.Ticket.CurrentDepartmentId == departmentId);
            candidates = candidates.Where(x => inDepartment.Any(m =>
                m.Kind == x.Kind
                && ((x.Kind == CrmKind && m.CrmId == x.CrmId)
                    || (x.Kind == ExternalKind && m.Source == x.Source && m.ExternalId == x.ExternalId)
                    || (x.Kind == PhoneKind && m.Phone == x.Phone))));
        }

        if (query.VerificationSource is { } source)
        {
            candidates = source.Equals("Crm", StringComparison.OrdinalIgnoreCase) ? candidates.Where(x => x.Kind == CrmKind)
                : source.Equals("Unverified", StringComparison.OrdinalIgnoreCase) ? candidates.Where(x => x.Kind == PhoneKind)
                : candidates.Where(x => x.Kind == ExternalKind && x.Source == source);
        }

        var grouped = candidates
            .GroupBy(x => new { x.Kind, x.CrmId, x.Source, x.ExternalId, x.Phone })
            .Select(g => new
            {
                g.Key.Kind,
                g.Key.CrmId,
                g.Key.Source,
                g.Key.ExternalId,
                g.Key.Phone,
                TotalTickets = g.Count(),
                OpenTickets = g.Count(x =>
                    x.Ticket.TicketStatus == TicketStatus.Open
                    || x.Ticket.TicketStatus == TicketStatus.InProgress
                    || x.Ticket.TicketStatus == TicketStatus.PendingCustomer
                    || x.Ticket.TicketStatus == TicketStatus.PendingThirdParty),
                // Ticket ids are assigned in creation order, so the highest id
                // is the latest ticket — the row's name/unit/phone snapshot.
                LastTicketId = g.Max(x => x.Ticket.TicketId),
                LastTicketCreatedAtUtc = g.Max(x => x.Ticket.CreatedAtUtc),
                LastInteractionAtUtc = g.Max(x => x.LastInteractionAtUtc),
            });

        if (query.OpenOnly)
        {
            grouped = grouped.Where(g => g.OpenTickets > 0);
        }

        var totalCount = await grouped.CountAsync(cancellationToken);
        var page = await grouped
            .OrderByDescending(g => g.LastTicketCreatedAtUtc)
            .ThenByDescending(g => g.LastTicketId)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        if (page.Count == 0)
        {
            return new CustomerDirectoryPage([], totalCount);
        }

        // Dress each row with its latest ticket's snapshot — one query per
        // fact table for the whole page, never one per row.
        var lastTicketIds = page.Select(g => g.LastTicketId).ToList();
        var lastTickets = await dbContext.Tickets.AsNoTracking()
            .Where(t => lastTicketIds.Contains(t.TicketId))
            .ToDictionaryAsync(t => t.TicketId, cancellationToken);
        var snapshots = await LoadSnapshotsAsync(lastTicketIds, cancellationToken);

        var rows = new List<CustomerDirectoryRowDto>(page.Count);
        foreach (var g in page)
        {
            var identity = ToIdentity(g.Kind, g.CrmId, g.Source, g.ExternalId, g.Phone);
            var last = lastTickets[g.LastTicketId];
            var snapshot = snapshots.GetValueOrDefault(g.LastTicketId);

            rows.Add(new CustomerDirectoryRowDto(
                identity.Key,
                identity.Kind.ToString(),
                DisplayNameFor(last, snapshot),
                identity.PhoneNumber ?? snapshot.IntakePhone ?? snapshot.InteractionPhone,
                last.CrmBuyerProjectName ?? last.ManualProjectName ?? snapshot.SnapshotProperty,
                last.CrmBuyerUnitNumber ?? last.ManualUnitNumber ?? snapshot.SnapshotUnit,
                VerificationSourceFor(identity),
                g.OpenTickets,
                g.TotalTickets,
                g.LastTicketId,
                last.TicketNumber,
                last.TicketStatus.ToString(),
                g.LastTicketCreatedAtUtc,
                g.LastInteractionAtUtc));
        }

        return new CustomerDirectoryPage(rows, totalCount);
    }

    public async Task<CustomerDirectoryProfileDto?> GetProfileAsync(
        CustomerIdentity identity, IReadOnlyCollection<int>? visibleDepartmentIds, CancellationToken cancellationToken = default)
    {
        var identified = Identified(visibleDepartmentIds);
        var own = identity.Kind switch
        {
            CustomerIdentityKind.Crm => identified.Where(x => x.Kind == CrmKind && x.CrmId == identity.CrmBuyerCustomerId),
            CustomerIdentityKind.External => identified.Where(x => x.Kind == ExternalKind && x.Source == identity.ExternalSource && x.ExternalId == identity.ExternalCustomerId),
            _ => identified.Where(x => x.Kind == PhoneKind && x.Phone == identity.PhoneNumber),
        };

        var tickets = await own
            .OrderByDescending(x => x.Ticket.CreatedAtUtc)
            .Select(x => new { x.Ticket, x.IntakePhone })
            .ToListAsync(cancellationToken);
        if (tickets.Count == 0)
        {
            return null;
        }

        var ticketIds = tickets.Select(x => x.Ticket.TicketId).ToList();
        var snapshots = await LoadSnapshotsAsync(ticketIds, cancellationToken);

        var lastStatusChange = await dbContext.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => ticketIds.Contains(h.TicketId))
            .GroupBy(h => h.TicketId)
            .Select(g => new { TicketId = g.Key, At = g.Max(h => h.OccurredAtUtc) })
            .ToDictionaryAsync(x => x.TicketId, x => x.At, cancellationToken);
        var lastNote = await dbContext.TicketNotes.AsNoTracking()
            .Where(n => ticketIds.Contains(n.TicketId))
            .GroupBy(n => n.TicketId)
            .Select(g => new { TicketId = g.Key, At = g.Max(n => n.CreatedAtUtc) })
            .ToDictionaryAsync(x => x.TicketId, x => x.At, cancellationToken);
        var resolvedAt = await dbContext.TicketResolutions.AsNoTracking()
            .Where(r => ticketIds.Contains(r.TicketId) && r.IsCurrent)
            .Select(r => new { r.TicketId, r.ResolvedAtUtc })
            .ToDictionaryAsync(x => x.TicketId, x => x.ResolvedAtUtc, cancellationToken);

        var channelNames = await dbContext.Channels.AsNoTracking()
            .Select(c => new { c.ChannelId, c.Name })
            .ToDictionaryAsync(c => c.ChannelId, c => c.Name, cancellationToken);
        var ticketNumbers = tickets.ToDictionary(x => x.Ticket.TicketId, x => x.Ticket.TicketNumber);
        var interactions = await dbContext.TicketInteractions.AsNoTracking()
            .Where(i => ticketIds.Contains(i.TicketId))
            .OrderByDescending(i => i.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        var ticketDtos = tickets.Select(x =>
        {
            var t = x.Ticket;
            var snapshot = snapshots.GetValueOrDefault(t.TicketId);
            var lastActivity = new[]
            {
                t.CreatedAtUtc,
                lastStatusChange.GetValueOrDefault(t.TicketId),
                lastNote.GetValueOrDefault(t.TicketId),
                resolvedAt.TryGetValue(t.TicketId, out var resolved) ? resolved : default,
            }.Max();

            return new CustomerDirectoryTicketDto(
                t.TicketId, t.TicketNumber, t.TicketStatus.ToString(), t.PriorityId, t.CurrentDepartmentId, t.CreatedAtUtc,
                t.CrmBuyerProjectName ?? t.ManualProjectName ?? snapshot.SnapshotProperty,
                t.CrmBuyerUnitNumber ?? t.ManualUnitNumber ?? snapshot.SnapshotUnit,
                t.RequestSummary,
                lastActivity,
                resolvedAt.TryGetValue(t.TicketId, out var r) ? r : null,
                t.VerificationStatus.ToString());
        }).ToList();

        var units = tickets
            .Select(x => new
            {
                Project = x.Ticket.CrmBuyerProjectName ?? x.Ticket.ManualProjectName ?? snapshots.GetValueOrDefault(x.Ticket.TicketId).SnapshotProperty,
                Unit = x.Ticket.CrmBuyerUnitNumber ?? x.Ticket.ManualUnitNumber ?? snapshots.GetValueOrDefault(x.Ticket.TicketId).SnapshotUnit,
                Source = x.Ticket.CrmBuyerUnitId != null ? "Crm" : x.Ticket.ExternalUnitId != null ? x.Ticket.CustomerVerificationSource ?? "External" : "Manual",
                x.Ticket.CrmBuyerUnitId,
                x.Ticket.ExternalUnitId,
                x.Ticket.CreatedAtUtc,
            })
            .Where(u => u.Project is not null || u.Unit is not null)
            .GroupBy(u => new { Project = u.Project?.Trim().ToUpperInvariant(), Unit = u.Unit?.Trim().ToUpperInvariant() })
            .Select(g =>
            {
                var latest = g.OrderByDescending(u => u.CreatedAtUtc).First();
                return new CustomerDirectoryUnitDto(latest.Project, latest.Unit, latest.Source, g.Count(), latest.CreatedAtUtc, latest.CrmBuyerUnitId, latest.ExternalUnitId);
            })
            .OrderByDescending(u => u.LastTicketAtUtc)
            .ToList();

        var phones = tickets.Select(x => x.IntakePhone)
            .Concat(interactions.Select(i => i.CustomerPhone))
            .Concat([identity.PhoneNumber])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var emails = interactions.Select(i => i.CustomerEmail)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var last = tickets[0].Ticket;
        var displayName = DisplayNameFor(last, snapshots.GetValueOrDefault(last.TicketId))
            ?? tickets.Select(x => x.Ticket.CrmBuyerCustomerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
            ?? interactions.Select(i => i.CustomerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

        return new CustomerDirectoryProfileDto(
            identity.Key,
            identity.Kind.ToString(),
            displayName,
            phones,
            emails,
            VerificationSourceFor(identity),
            identity.CrmBuyerCustomerId,
            identity.ExternalSource,
            identity.ExternalCustomerId,
            ticketDtos.Count(t => t.TicketStatus is "Open" or "InProgress" or "PendingCustomer" or "PendingThirdParty"),
            ticketDtos.Count,
            tickets[^1].Ticket.CreatedAtUtc,
            last.CreatedAtUtc,
            last.TicketId,
            units,
            ticketDtos,
            interactions.Select(i => new CustomerDirectoryInteractionDto(
                i.TicketInteractionId, i.TicketId, ticketNumbers.GetValueOrDefault(i.TicketId, string.Empty), i.ChannelId,
                channelNames.GetValueOrDefault(i.ChannelId), i.Direction, i.InteractionStartedAtUtc ?? i.CreatedAtUtc, i.EndedAtUtc,
                i.EndedAtUtc is null ? "Active" : "Ended", i.GenesysAgentName, i.IsOriginatingInteraction)).ToList());
    }

    // ---- helpers ----

    /// <summary>The per-ticket facts that are not on the Ticket row itself: intake phone, originating interaction name/phone, requester snapshot.</summary>
    private readonly record struct TicketSnapshot(string? IntakePhone, string? InteractionName, string? InteractionPhone, string? SnapshotName, string? SnapshotProperty, string? SnapshotUnit);

    private async Task<Dictionary<long, TicketSnapshot>> LoadSnapshotsAsync(List<long> ticketIds, CancellationToken cancellationToken)
    {
        var intakes = await dbContext.IntakeRecords.AsNoTracking()
            .Where(i => i.LinkedTicketId != null && ticketIds.Contains(i.LinkedTicketId.Value) && i.PhoneNumber != "")
            .OrderBy(i => i.IntakeRecordId)
            .Select(i => new { TicketId = i.LinkedTicketId!.Value, i.PhoneNumber })
            .ToListAsync(cancellationToken);
        var originating = await dbContext.TicketInteractions.AsNoTracking()
            .Where(i => ticketIds.Contains(i.TicketId))
            .OrderByDescending(i => i.IsOriginatingInteraction).ThenBy(i => i.TicketInteractionId)
            .Select(i => new { i.TicketId, i.CustomerName, i.CustomerPhone })
            .ToListAsync(cancellationToken);
        var requester = await dbContext.TicketRequesterSnapshots.AsNoTracking()
            .Where(s => ticketIds.Contains(s.TicketId))
            .Select(s => new { s.TicketId, s.SnapshotContactDisplayName, s.SnapshotPropertyName, s.SnapshotUnitNumber })
            .ToListAsync(cancellationToken);

        var intakeByTicket = intakes.GroupBy(i => i.TicketId).ToDictionary(g => g.Key, g => g.First().PhoneNumber);
        var interactionByTicket = originating.GroupBy(i => i.TicketId).ToDictionary(g => g.Key, g => g.First());
        var requesterByTicket = requester.GroupBy(s => s.TicketId).ToDictionary(g => g.Key, g => g.First());

        return ticketIds.Distinct().ToDictionary(id => id, id => new TicketSnapshot(
            intakeByTicket.GetValueOrDefault(id),
            interactionByTicket.GetValueOrDefault(id)?.CustomerName,
            interactionByTicket.GetValueOrDefault(id)?.CustomerPhone,
            requesterByTicket.GetValueOrDefault(id)?.SnapshotContactDisplayName,
            requesterByTicket.GetValueOrDefault(id)?.SnapshotPropertyName,
            requesterByTicket.GetValueOrDefault(id)?.SnapshotUnitNumber));
    }

    private static CustomerIdentity ToIdentity(int kind, int? crmId, string? source, string? externalId, string? phone) => kind switch
    {
        CrmKind => CustomerIdentity.Crm(crmId!.Value),
        ExternalKind => CustomerIdentity.External(source!, externalId!),
        _ => CustomerIdentity.Phone(phone!),
    };

    private static string VerificationSourceFor(CustomerIdentity identity) => identity.Kind switch
    {
        CustomerIdentityKind.Crm => "Crm",
        CustomerIdentityKind.External => identity.ExternalSource!,
        _ => "Unverified",
    };

    /// <summary>The best persisted name: the CRM Buyer snapshot, else what the originating call/chat reported, else the legacy requester snapshot — never an id dressed as a name.</summary>
    private static string? DisplayNameFor(Ticket last, TicketSnapshot snapshot) =>
        FirstNonEmpty(last.CrmBuyerCustomerName, snapshot.InteractionName, snapshot.SnapshotName);

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
