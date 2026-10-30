using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Api.Filters;

/// <summary>
/// Refuses a request that addresses a ticket (<c>{ticketId}</c> in the route) the caller may not see — the server-side half of CS Agent
/// ticket visibility for direct URLs and API calls. A CS Agent sees only tickets assigned to them or on which they handled a Genesys
/// interaction (<see cref="TicketVisibilityRule"/>); without this, every endpoint under <c>api/tickets/{ticketId}</c> would have to
/// remember that, and the CS Agent's cross-department action rights (close, classify, reopen, escalate, …) would reach tickets they cannot
/// even open.
///
/// <para>
/// It is a <i>resource</i> filter, so it runs before model binding and validation: a restricted agent is refused (403) before the request
/// body is even read, and a malformed body cannot turn the answer into a 400 that reveals the ticket exists. It only ever acts for a
/// restricted CS Agent: every other caller passes straight through without a database
/// read, so Admin/Supervisor/Manager/department behaviour is exactly what each endpoint's own checks already make it. A ticket that does
/// not exist is left to the action (404, as before); one that exists but is not visible is <b>403</b> — the same answer the ticket detail
/// has always given for an out-of-scope ticket. The services keep their own checks; this is the net underneath them.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireTicketAccessAttribute() : TypeFilterAttribute(typeof(RequireTicketAccessFilter))
{
}

internal sealed class RequireTicketAccessFilter(TicketQueryAppService ticketQueryAppService) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!TryGetTicketId(context, out var ticketId))
        {
            await next();
            return;
        }

        var roles = context.HttpContext.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
        if (!TicketVisibilityRule.IsRestrictedCsAgent(roles))
        {
            await next();
            return;
        }

        var idValue = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is null || !Guid.TryParse(idValue, out var employeeId))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        // The Genesys integration account is a "CS Agent" by permission set but not a person, and is already confined to the integration surface.
        if (!ticketQueryAppService.IsRestrictedAgent(employeeId, roles))
        {
            await next();
            return;
        }

        var access = await ticketQueryAppService.CheckAccessAsync(employeeId, roles, ticketId, context.HttpContext.RequestAborted);
        if (access == TicketAccess.Denied)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Ticket not accessible.",
                Detail = "You can only access tickets assigned to you or tickets on which you handled a Genesys interaction."
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        await next();
    }

    private static bool TryGetTicketId(ResourceExecutingContext context, out long ticketId)
    {
        ticketId = 0;
        return context.RouteData.Values.TryGetValue("ticketId", out var raw)
            && long.TryParse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture), out ticketId);
    }
}
