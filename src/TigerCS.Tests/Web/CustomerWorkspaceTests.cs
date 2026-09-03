// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj — because
// its own top-level-statement Program type would otherwise collide with
// TigerCS.Api's, which existing WebApplicationFactory<Program> tests use unqualified.
extern alias TigerCsWeb;

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Models;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Customer Workspace (Dashboard + Customers pages): search-first flow,
/// customer summary, ticket history across ALL units with an optional unit
/// filter (never a required unit selection), Reopen affordances gated on the
/// server-computed lifecycle flag plus CS-layer roles, and New Ticket
/// carry-forward that never makes the agent re-search the same customer.
/// </summary>
public sealed class CustomerWorkspaceTests
{
    private const string Phone = "+971501112233";

    // ---------------------------------------------------------------
    // PageModel plumbing
    // ---------------------------------------------------------------

    private static ClaimsPrincipal PrincipalWithRoles(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Test Agent")
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static void GivePageContext(PageModel model, ClaimsPrincipal principal) =>
        model.PageContext = new PageContext(new ActionContext(
            new DefaultHttpContext { User = principal }, new RouteData(), new PageActionDescriptor()));

    private static TicketNameResolver NameResolver()
    {
        // users/me failing is a normal, tolerated outcome for the resolver —
        // names simply fall back to id text.
        var usersHandler = new FakeApiHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        return new TicketNameResolver(new UsersApiClient(
            new HttpClient(usersHandler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<UsersApiClient>.Instance));
    }

    private static (CustomersModel Model, FakeApiHandler Customers) CreateCustomersModel(
        Func<HttpRequestMessage, string?, HttpResponseMessage> customersResponder,
        params string[] roles)
    {
        var handler = new FakeApiHandler(customersResponder);
        var client = new CustomerHistoryApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<CustomerHistoryApiClient>.Instance);
        var model = new CustomersModel(client, NameResolver());
        GivePageContext(model, PrincipalWithRoles(roles.Length == 0 ? [Roles.CsAgent] : roles));
        return (model, handler);
    }

    // ---------------------------------------------------------------
    // Search + summary + history fixtures
    // ---------------------------------------------------------------

    private static CrmBuyerMatchDto CrmBuyer(int customerId = 9001, string name = "Sami Nasser") => new(
        new CrmCustomerDto(customerId, name, null, Phone, "sami@example.test"),
        [
            new CrmBuyerUnitDto(1, 4, "Contract", 101, "1506", 1, 2, 15, 10, "Nobles Tower", null, 1, "Buyer"),
            new CrmBuyerUnitDto(2, 4, "Contract", 102, "1204", 1, 2, 12, 10, "Nobles Tower", null, 1, "Buyer")
        ]);

    private static CustomerSearchResultDto CrmOnlySearchResult(CrmBuyerMatchDto? buyer = null) => new(
        Phone, "Found", [buyer ?? CrmBuyer()],
        [CustomerLookupSourceResultDto.NotFound("Pact"), CustomerLookupSourceResultDto.NotFound("Tasleeh")]);

    private static CustomerHistoryTicketDto HistoryRow(
        long id, string unit, string status = "Resolved", bool reopenEligible = false, string summary = "AC issue") => new(
        id, $"TG-CS-20260901-{id:D4}", DateTime.UtcNow.AddDays(-id), status, 3, 5, 2, "Nobles Tower", unit, "Verified",
        summary, status is "Resolved" or "Closed" ? DateTime.UtcNow.AddDays(-1) : null, reopenEligible);

    private static CustomerHistoryDto History(params CustomerHistoryTicketDto[] rows) => new(
        "Verified", 9001, null, "Sami Nasser",
        rows.Length, rows.Count(r => r.TicketStatus is not ("Resolved" or "Closed")),
        rows.Count(r => r.TicketStatus is "Resolved" or "Closed"), rows);

    private static Func<HttpRequestMessage, string?, HttpResponseMessage> RespondingWith(
        CustomerSearchResultDto search, object history) =>
        (request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/customers/search" => FakeApiHandler.JsonResponse(HttpStatusCode.OK, search),
            var path when path.EndsWith("/ticket-history", StringComparison.Ordinal) =>
                FakeApiHandler.JsonResponse(HttpStatusCode.OK, history),
            var path => throw new InvalidOperationException($"Unexpected call: {path}")
        };

    // ---------------------------------------------------------------
    // Customer search returns the correct customer, and history never
    // requires a unit selection first
    // ---------------------------------------------------------------

    [Fact]
    public async Task OnGet_SingleCrmMatch_AutoSelects_AndLoadsHistoryByTheStableCrmCustomerId()
    {
        var (model, handler) = CreateCustomersModel(
            RespondingWith(CrmOnlySearchResult(), History(HistoryRow(1, "1506"), HistoryRow(2, "1204"))));

        await model.OnGetAsync(Phone, customer: null, unit: null, CancellationToken.None);

        Assert.NotNull(model.Selected);
        Assert.Equal("Crm", model.Selected!.Source);
        Assert.Equal(9001, model.Selected.CrmCustomerId);
        // Identity, not name: history is fetched by the CRM customer id.
        Assert.Contains(handler.Requests, r => r.RequestUri.Contains("/api/customers/crm/9001/ticket-history"));
        // All units are shown with no unit selection made.
        Assert.Null(model.UnitFilter);
        Assert.Equal(2, model.FilteredTickets.Count);
        Assert.Equal(["1506", "1204"], model.FilteredTickets.Select(t => t.UnitNumber));
    }

    [Fact]
    public async Task OnGet_UnitFilter_NarrowsTheTicketList_ButAllUnitsRemainsTheDefault()
    {
        var (model, _) = CreateCustomersModel(
            RespondingWith(CrmOnlySearchResult(), History(HistoryRow(1, "1506"), HistoryRow(2, "1204"))));

        await model.OnGetAsync(Phone, customer: null, unit: "1204", CancellationToken.None);

        Assert.Equal("1204", model.UnitFilter);
        Assert.Equal("1204", Assert.Single(model.FilteredTickets).UnitNumber);
        // Option list still spans every known unit so the agent can go back to All Units.
        Assert.Contains("1506", model.UnitOptions);
        Assert.Contains("1204", model.UnitOptions);
    }

    [Fact]
    public async Task OnGet_PactMatch_LoadsHistoryByThePersistedExternalIdentity_NeverByName()
    {
        var search = new CustomerSearchResultDto(
            Phone, "NotFound", [],
            [
                CustomerLookupSourceResultDto.Found("Pact",
                    [new CustomerLookupCustomerDto("PACT-CUST-77", "Aisha Rahman", Phone, null, "Tenant",
                        [new CustomerLookupUnitDto("PU-1", "1506", "Marina Heights", null, "Apartment", null, null)])]),
                CustomerLookupSourceResultDto.NotFound("Tasleeh")
            ]);
        var externalHistory = new CustomerHistoryDto(
            "ExternalVerified", null, null, "Aisha Rahman", 1, 0, 1,
            [HistoryRow(7, "1506")], "Pact", "PACT-CUST-77");
        var (model, handler) = CreateCustomersModel(RespondingWith(search, externalHistory));

        await model.OnGetAsync(Phone, customer: null, unit: null, CancellationToken.None);

        Assert.NotNull(model.Selected);
        Assert.Equal("Pact", model.Selected!.Source);
        Assert.Equal("PACT-CUST-77", model.Selected.ExternalCustomerId);
        Assert.Contains(handler.Requests, r => r.RequestUri.Contains("/api/customers/external/Pact/PACT-CUST-77/ticket-history"));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri.Contains("Aisha"));
        Assert.Single(model.FilteredTickets);
    }

    [Fact]
    public async Task OnGet_MultipleCandidates_RendersThePickerInsteadOfGuessing()
    {
        var search = new CustomerSearchResultDto(
            Phone, "Found", [CrmBuyer()],
            [
                CustomerLookupSourceResultDto.Found("Pact",
                    [new CustomerLookupCustomerDto("PACT-CUST-77", "Aisha Rahman", Phone, null, null, [])]),
                CustomerLookupSourceResultDto.NotFound("Tasleeh")
            ]);
        var (model, handler) = CreateCustomersModel(RespondingWith(search, new object()));

        await model.OnGetAsync(Phone, customer: null, unit: null, CancellationToken.None);

        Assert.Null(model.Selected);
        Assert.Equal(2, model.Candidates.Count);
        // No history call happens until the agent picks who the caller is.
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri.Contains("ticket-history"));
    }

    [Fact]
    public async Task OnGet_ExplicitCandidateSelection_WinsOverAutoSelection()
    {
        var search = new CustomerSearchResultDto(
            Phone, "Found", [CrmBuyer()],
            [
                CustomerLookupSourceResultDto.Found("Pact",
                    [new CustomerLookupCustomerDto("PACT-CUST-77", "Aisha Rahman", Phone, null, null, [])]),
                CustomerLookupSourceResultDto.NotFound("Tasleeh")
            ]);
        var externalHistory = new CustomerHistoryDto("ExternalVerified", null, null, "Aisha Rahman", 0, 0, 0, [], "Pact", "PACT-CUST-77");
        var (model, handler) = CreateCustomersModel(RespondingWith(search, externalHistory));

        await model.OnGetAsync(Phone, customer: "ext:Pact:PACT-CUST-77", unit: null, CancellationToken.None);

        Assert.Equal("Pact", model.Selected!.Source);
        Assert.Contains(handler.Requests, r => r.RequestUri.Contains("/api/customers/external/Pact/PACT-CUST-77/ticket-history"));
    }

    // ---------------------------------------------------------------
    // Reopen affordance gating (display side — the Api still enforces)
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(Roles.CsAgent, true)]
    [InlineData(Roles.CsSupervisor, true)]
    [InlineData(Roles.CsManager, true)]
    [InlineData(Roles.SystemAdministrator, true)]
    [InlineData(Roles.DepartmentEmployee, false)]
    [InlineData(Roles.DepartmentHead, false)]
    [InlineData(Roles.GeneralManager, false)]
    public void CanReopen_MirrorsTheCsLayerReopenRoleSet(string role, bool expected) =>
        Assert.Equal(expected, TicketActions.CanReopen([role]));

    [Fact]
    public async Task ViewerCanReopen_IsFalseForADepartmentEmployee_SoNoReopenControlRenders()
    {
        var (model, _) = CreateCustomersModel(
            RespondingWith(CrmOnlySearchResult(), History(HistoryRow(1, "1506", reopenEligible: true))),
            Roles.DepartmentEmployee);

        await model.OnGetAsync(Phone, customer: null, unit: null, CancellationToken.None);

        Assert.False(model.ViewerCanReopen);
        Assert.True(Assert.Single(model.FilteredTickets).IsReopenEligible);
    }

    // ---------------------------------------------------------------
    // View contracts (source inspection, same style as CustomerProfileTests)
    // ---------------------------------------------------------------

    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string CustomersViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "Customers.cshtml")));

    private static string DashboardViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "Dashboard.cshtml")));

    private static string TicketDetailsViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml")));

    [Fact]
    public void CustomersView_HasThreeTabs_WithTicketsSelectedByDefault()
    {
        var html = CustomersViewHtml();

        Assert.Contains("id=\"tab-cust-tickets\"", html);
        Assert.Contains("id=\"tab-cust-units\"", html);
        Assert.Contains("id=\"tab-cust-info\"", html);

        var ticketsTabStart = html.IndexOf("id=\"tab-cust-tickets\"", StringComparison.Ordinal);
        var ticketsTabEnd = html.IndexOf("/>", ticketsTabStart, StringComparison.Ordinal);
        Assert.Contains("checked", html[ticketsTabStart..ticketsTabEnd]);
    }

    [Fact]
    public void CustomersView_TicketList_UsesTheOneLineTruncatedSummary_NeverAFullDescriptionBlock()
    {
        var html = CustomersViewHtml();

        Assert.Contains("cell-truncate", html);
        Assert.Contains("row.RequestSummary", html);
    }

    [Fact]
    public void CustomersView_UnitFilterDefaultsToAllUnits()
    {
        var html = CustomersViewHtml();

        Assert.Contains("<option value=\"\">All Units</option>", html);
    }

    [Fact]
    public void CustomersView_ReopenLink_IsGatedOnServerEligibilityAndViewerRole()
    {
        var html = CustomersViewHtml();

        Assert.Contains("row.IsReopenEligible && Model.ViewerCanReopen", html);
        Assert.Contains("?reopen=1", html);
    }

    [Fact]
    public void CustomersView_NewTicketButton_CarriesTheSearchedPhoneForward()
    {
        var html = CustomersViewHtml();

        Assert.Contains("/NewTicket?phoneNumber=", html);
    }

    [Fact]
    public void DashboardView_HasProminentCustomerSearch_AndNewTicketButton()
    {
        var html = DashboardViewHtml();

        Assert.Contains("action=\"/Customers\"", html);
        Assert.Contains("name=\"phoneNumber\"", html);
        Assert.Contains("href=\"/NewTicket\"", html);
        Assert.Contains("Tickets Requiring Attention", html);
        // Attention rows are compact: one-line summary, never a description block.
        Assert.Contains("cell-truncate", html);
    }

    private static string NewTicketViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "NewTicket.cshtml")));

    [Fact]
    public void NewTicketView_RelatedTicketsPanel_RendersOnlyWhenRelatedTicketsExist()
    {
        var html = NewTicketViewHtml();

        Assert.Contains("Related tickets found", html);
        Assert.Contains("Model.RelatedTickets is not null && Model.RelatedTickets.Tickets.Count > 0", html);
    }

    [Fact]
    public void NewTicketView_RelatedTicketsPanel_ShowsOneLineSummaries_NeverAFullDescription()
    {
        var html = NewTicketViewHtml();
        var panelStart = html.IndexOf("related-tickets", StringComparison.Ordinal);
        var panelEnd = html.IndexOf("Previous Tickets", panelStart, StringComparison.Ordinal);
        var panel = html[panelStart..panelEnd];

        Assert.Contains("cell-truncate", panel);
        Assert.Contains("row.RequestSummary", panel);
    }

    [Fact]
    public void NewTicketView_RelatedTicketsPanel_ContinueWithNewTicket_IsAlwaysAvailable()
    {
        var html = NewTicketViewHtml();

        // The advisory panel links down to the creation form — creation is
        // never blocked by a related ticket.
        Assert.Contains(">Continue with New Ticket</a>", html);
        Assert.Contains("href=\"#new-ticket-form\"", html);
        Assert.Contains("id=\"new-ticket-form\"", html);
    }

    [Fact]
    public void NewTicketView_RelatedTicketsPanel_ActionsFollowStatusAndReopenPolicy()
    {
        var html = NewTicketViewHtml();
        var panelStart = html.IndexOf("related-tickets", StringComparison.Ordinal);
        var panelEnd = html.IndexOf("Previous Tickets", panelStart, StringComparison.Ordinal);
        var panel = html[panelStart..panelEnd];

        // View for finished tickets, Open for active ones — same link, honest label.
        Assert.Contains("@(isFinished ? \"View\" : \"Open\")", panel);
        // Reopen renders only from the server-computed ReopenPolicy flag +
        // the CS-layer role check — no eligibility re-derived in the page.
        Assert.Contains("row.IsReopenEligible && viewerCanReopen", panel);
        Assert.Contains("?reopen=1", panel);
        Assert.DoesNotContain("ReopenWindow", panel);
    }

    [Fact]
    public void TicketDetailsView_ReopenControl_IsGatedOnServerEligibilityAndViewerRole()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("t.IsReopenEligible && TicketActions.CanReopen(Model.Viewer?.Roles)", html);
        Assert.Contains("asp-page-handler=\"Reopen\"", html);
    }

    // ---------------------------------------------------------------
    // Dashboard role-appropriate KPI cards
    // ---------------------------------------------------------------

    private static DashboardModel CreateDashboardModel(DashboardSummaryDto summary, params string[] roles)
    {
        var handler = new FakeApiHandler((_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, summary));
        var client = new DashboardApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<DashboardApiClient>.Instance);
        var model = new DashboardModel(client, NameResolver());
        GivePageContext(model, PrincipalWithRoles(roles));
        return model;
    }

    private static DashboardSummaryDto Summary() => new(12, 3, 2, 1, 4, 5, 6, 1, 2, []);

    [Fact]
    public async Task Dashboard_SupervisoryRoles_SeeQueueHealthCards()
    {
        var model = CreateDashboardModel(Summary(), Roles.CsSupervisor);

        await model.OnGetAsync(CancellationToken.None);

        var labels = model.Cards.Select(c => c.Label).ToArray();
        Assert.Contains("Unassigned", labels);
        Assert.Contains("SLA Breached", labels);
        Assert.Contains("Reopened", labels);
        Assert.DoesNotContain("My Tickets", labels);
    }

    [Fact]
    public async Task Dashboard_CsAgent_SeesTheirOwnWorkloadCards()
    {
        var model = CreateDashboardModel(Summary(), Roles.CsAgent);

        await model.OnGetAsync(CancellationToken.None);

        var labels = model.Cards.Select(c => c.Label).ToArray();
        Assert.Equal(["My Tickets", "Open Tickets", "SLA At Risk", "Pending Customer"], labels);
    }

    [Fact]
    public async Task Dashboard_DepartmentUser_SeesTheirDepartmentQueueCards()
    {
        var model = CreateDashboardModel(Summary(), Roles.DepartmentEmployee);

        await model.OnGetAsync(CancellationToken.None);

        var labels = model.Cards.Select(c => c.Label).ToArray();
        Assert.Equal(["My Tickets", "Open Tickets", "SLA At Risk", "SLA Breached"], labels);
    }
}
