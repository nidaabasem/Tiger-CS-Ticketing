using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// The customer-friendly "subject" of a ticket, for the Resolved and Closed emails. Tickets carry no title of their own, so the
/// subject is the customer-recognisable classification — the request type, else the category — and, only for a ticket that
/// was never classified, the first line of the request summary the customer's own request was logged with, shortened.
/// </summary>
public sealed class CustomerTicketSubjectResolver(IRequestTypeRepository requestTypeRepository, ICategoryRepository categoryRepository)
{
    public const int MaxSummaryLength = 120;

    public async Task<string?> ResolveAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.RequestTypeId is { } requestTypeId
            && (await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken))?.Name is { } typeName
            && !string.IsNullOrWhiteSpace(typeName))
        {
            return typeName.Trim();
        }

        if (ticket.CategoryId is { } categoryId
            && (await categoryRepository.GetByIdAsync(categoryId, cancellationToken))?.Name is { } categoryName
            && !string.IsNullOrWhiteSpace(categoryName))
        {
            return categoryName.Trim();
        }

        return FirstLine(ticket.RequestSummary);
    }

    internal static string? FirstLine(string? summary)
    {
        var line = summary?.Split('\n', '\r').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null)
        {
            return null;
        }

        return line.Length <= MaxSummaryLength ? line : line[..(MaxSummaryLength - 1)].TrimEnd() + "…";
    }
}
