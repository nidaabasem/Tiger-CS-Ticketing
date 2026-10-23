using TigerCS.Web.Pages.Admin;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Models;

/// <summary>
/// Why the Classify form cannot be completed for a ticket, in words an employee can act on — and where the fix lives.
/// </summary>
/// <param name="Headline">What the attention card says.</param>
/// <param name="Detail">The reason and the next step.</param>
/// <param name="Short">One line for the "not available" list.</param>
/// <param name="AdminHref">The existing Administration screen that resolves it; only set for a viewer who may open it.</param>
/// <param name="AdminLabel">The link text for <paramref name="AdminHref"/>.</param>
/// <param name="IsConfiguration">True when the configuration genuinely has nothing to offer; false for a failed read, where reloading is the remedy.</param>
public sealed record ClassifyNotice(string Headline, string Detail, string Short, string? AdminHref, string? AdminLabel, bool IsConfiguration)
{
    /// <summary>
    /// The notice for <paramref name="options"/>, or null when the form can be completed. Nothing here creates, routes or
    /// seeds anything: it only names the existing administrative path.
    /// </summary>
    public static ClassifyNotice? For(TicketClassifyOptions options, string departmentName, CurrentUser? viewer, bool canTransfer)
    {
        switch (options.Availability)
        {
            case ClassifyAvailability.Available:
                return null;

            case ClassifyAvailability.LoadFailed:
                return new ClassifyNotice(
                    "Classification options could not be loaded",
                    "The category and request-type lists did not load, so the ticket cannot be classified right now. Reload the page; if it keeps happening, tell your administrator.",
                    "The category and request-type lists could not be loaded.",
                    null, null, IsConfiguration: false);

            default:
                var isAdministrator = AdministrationPolicy.AppliesTo(viewer);
                var transferHint = canTransfer ? " Or transfer the ticket to the department that owns the request." : "";
                var detail = $"{departmentName} has no active category to classify a ticket under, and classifying never moves a ticket to another department. "
                    + (isAdministrator
                        ? $"Request types for {departmentName} are managed under Administration → Request Types; each also needs an active routing category of the same name, which the request-type import adds."
                        : "Ask a System Administrator to set up categories and request types for this department.")
                    + transferHint;
                return new ClassifyNotice(
                    $"{departmentName} has no classification options set up",
                    detail,
                    $"{departmentName} has no active category or request type configured.",
                    isAdministrator ? AdminModules.RequestTypes.Href : null,
                    isAdministrator ? "Open Request Types" : null,
                    IsConfiguration: true);
        }
    }
}
