// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details' Change Status picker: that it offers exactly the targets
/// the Api would accept from the ticket's CURRENT status, and nothing else.
///
/// <para>
/// The behaviour these pin down is the approved lifecycle cleanup. The picker
/// used to render a fixed four-item list — Open, In Progress, Pending
/// Customer, Pending Third Party — on every ticket regardless of its status,
/// so three of the four were usually an invitation to a 422, and the fourth
/// was a status the business has retired. It now reads the domain's own
/// transition table, which is also what the endpoint enforces, so the two
/// cannot disagree.
/// </para>
///
/// <para>
/// These are display assertions. Enforcement lives in
/// <c>Ticket.ChangeStatus</c> and <c>TicketLifecycleAppService</c> and is
/// covered by <c>TicketTests</c> and <c>TicketWorkflowEnforcementTests</c> —
/// hiding a control is never the rule.
/// </para>
/// </summary>
public sealed class TicketStatusPickerTests
{
    private const int TicketDepartmentId = 1;
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ---------------------------------------------------------------
    // Harness — the viewer always has Change Status authority, so every
    // assertion below is about the LIFECYCLE half and nothing else.
    // ---------------------------------------------------------------

    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string TicketDetailsViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml")));

    private static ClaimsPrincipal Principal(Guid employeeId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, employeeId.ToString()),
            new(ClaimTypes.Name, "Test Viewer")
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static TicketDetailDto Ticket(string ticketStatus) => new(
        1, "TG-FM-20260905-0001", TicketDepartmentId, TicketDepartmentId, Owner, null, null,
        5, 3, ticketStatus, "Verified", "None", "Running", null, null, "AC not cooling", 0,
        DateTime.UtcNow, Convert.ToBase64String([1, 2, 3, 4]));

    private static async Task<TicketDetailsModel> RenderAsync(string ticketStatus)
    {
        string[] roles = [Roles.DepartmentEmployee];

        var handler = new FakeApiHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/api/users/me")
            {
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CurrentUserResponseDto(
                    Owner, "Test Viewer", roles,
                    [new DepartmentMembershipDto(TicketDepartmentId, "Facilities", IsPrimary: true)],
                    IsGeynessStaff: false));
            }

            if (path == "/api/tickets/1" && request.Method == HttpMethod.Get)
            {
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, Ticket(ticketStatus));
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpClient Client() => new(handler) { BaseAddress = new Uri("http://localhost/") };

        var users = new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance);
        var model = new TicketDetailsModel(
            new TicketsApiClient(Client(), NullLogger<TicketsApiClient>.Instance),
            new TicketSlaApiClient(Client(), NullLogger<TicketSlaApiClient>.Instance),
            users,
            new TicketNameResolver(users, new DepartmentsApiClient(Client(), NullLogger<DepartmentsApiClient>.Instance)))
        {
            PageContext = new PageContext(new ActionContext(
                new DefaultHttpContext { User = Principal(Owner, roles) }, new RouteData(), new PageActionDescriptor()))
        };

        await model.OnGetAsync(1, null, CancellationToken.None);
        return model;
    }

    // ---------------------------------------------------------------
    // The picker offers exactly the legal next statuses
    // ---------------------------------------------------------------

    [Fact]
    public async Task OpenTicket_IsOfferedInProgressAndNothingElse()
    {
        var model = await RenderAsync(nameof(TicketStatus.Open));

        Assert.True(model.CanChangeStatus);
        Assert.Equal([TicketStatus.InProgress], model.StatusTargets);
    }

    [Fact]
    public async Task InProgressTicket_IsOfferedPendingCustomerAndNothingElse()
    {
        var model = await RenderAsync(nameof(TicketStatus.InProgress));

        Assert.True(model.CanChangeStatus);
        Assert.Equal([TicketStatus.PendingCustomer], model.StatusTargets);
    }

    [Fact]
    public async Task PendingCustomerTicket_IsOfferedInProgressAndNothingElse()
    {
        var model = await RenderAsync(nameof(TicketStatus.PendingCustomer));

        Assert.True(model.CanChangeStatus);
        Assert.Equal([TicketStatus.InProgress], model.StatusTargets);
    }

    [Fact]
    public async Task LegacyPendingThirdPartyTicket_IsOfferedInProgressAndNothingElse_ItsEscapePath()
    {
        // A historical ticket still renders, and the one move it has is the
        // one the business sanctioned: back into the active lifecycle.
        var model = await RenderAsync(nameof(TicketStatus.PendingThirdParty));

        Assert.NotNull(model.Ticket);
        Assert.Equal(nameof(TicketStatus.PendingThirdParty), model.Ticket.TicketStatus);

        Assert.True(model.CanChangeStatus);
        Assert.Equal([TicketStatus.InProgress], model.StatusTargets);
    }

    // ---------------------------------------------------------------
    // What the picker never offers
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(nameof(TicketStatus.Open))]
    [InlineData(nameof(TicketStatus.InProgress))]
    [InlineData(nameof(TicketStatus.PendingCustomer))]
    [InlineData(nameof(TicketStatus.PendingThirdParty))]
    public async Task ThePicker_NeverOffersTheRetiredStatus_OpenAsATarget_OrATerminalStatus(string from)
    {
        var model = await RenderAsync(from);

        Assert.DoesNotContain(TicketStatus.PendingThirdParty, model.StatusTargets);

        // Open is never a target: a started ticket does not become unstarted.
        Assert.DoesNotContain(TicketStatus.Open, model.StatusTargets);

        // Resolve, Close and Reopen stay dedicated actions.
        Assert.DoesNotContain(TicketStatus.Resolved, model.StatusTargets);
        Assert.DoesNotContain(TicketStatus.Closed, model.StatusTargets);
    }

    // ---------------------------------------------------------------
    // No legal target means no control at all
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(nameof(TicketStatus.Resolved))]
    [InlineData(nameof(TicketStatus.Closed))]
    public async Task TerminalTicket_HidesTheChangeStatusActionEntirely(string terminalStatus)
    {
        var model = await RenderAsync(terminalStatus);

        Assert.Empty(model.StatusTargets);
        Assert.False(model.CanChangeStatus);
    }

    [Fact]
    public async Task AnUnrecognizedStatus_HidesTheAction_RatherThanGuessingATarget()
    {
        var model = await RenderAsync("SomethingTheClientDoesNotKnow");

        Assert.Empty(model.StatusTargets);
        Assert.False(model.CanChangeStatus);
    }

    // ---------------------------------------------------------------
    // The default the untouched form would submit
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(nameof(TicketStatus.Open), nameof(TicketStatus.InProgress))]
    [InlineData(nameof(TicketStatus.InProgress), nameof(TicketStatus.PendingCustomer))]
    [InlineData(nameof(TicketStatus.PendingCustomer), nameof(TicketStatus.InProgress))]
    [InlineData(nameof(TicketStatus.PendingThirdParty), nameof(TicketStatus.InProgress))]
    public async Task ThePicker_DefaultsToATargetItActuallyOffers(string from, string expectedDefault)
    {
        // Defaulting to the ticket's own current status — as the form used to
        // — made an untouched submit a guaranteed invalid transition.
        var model = await RenderAsync(from);

        Assert.Equal(expectedDefault, model.Status.NewStatus);
        Assert.Contains(model.StatusTargets, s => s.ToString() == model.Status.NewStatus);
    }

    // ---------------------------------------------------------------
    // The view carries no status list of its own
    // ---------------------------------------------------------------

    [Fact]
    public void TheView_BuildsThePickerFromTheModel_NeverFromALiteralStatusList()
    {
        // A literal list in the view would be a second transition rule, free
        // to drift from the domain's — which is exactly how Open and Pending
        // Third Party came to be offered.
        var html = TicketDetailsViewHtml();

        Assert.Contains("@foreach (var s in Model.StatusTargets)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PendingThirdParty\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("new[] { \"Open\", \"InProgress\"", html, StringComparison.Ordinal);
    }
}
