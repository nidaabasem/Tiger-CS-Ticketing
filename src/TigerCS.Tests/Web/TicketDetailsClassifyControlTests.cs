// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details' Classify control for an Unclassified ticket: offered to employees who can work the ticket, loaded with the ticket's own
/// department's categories and request types, absent for read-only roles, classified or closed tickets — and never a handoff.
/// </summary>
public sealed class TicketDetailsClassifyControlTests
{
    private const int DepartmentId = 4;
    private static readonly Guid Viewer = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ClaimsPrincipal Principal(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Viewer.ToString()), new(ClaimTypes.Name, "Test Viewer") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static TicketDetailDto Ticket(int? categoryId, string status = "Open", int? requestTypeId = null) => new(
        1, "TG-LCS-20261021-0002", DepartmentId, DepartmentId, null, null, null,
        categoryId, 3, status, "Unverified", "None", "Running", null, null, "Website chat started via Genesys", 0,
        DateTime.UtcNow, Convert.ToBase64String([1, 2, 3, 4]), RequestTypeId: requestTypeId);

    private static (TicketDetailsModel Model, List<string> Calls) Build(TicketDetailDto ticket, params string[] roles)
    {
        var calls = new List<string>();
        var handler = new FakeApiHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add($"{request.Method} {path}{request.RequestUri.Query}");
            return path switch
            {
                "/api/users/me" => FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CurrentUserResponseDto(
                    Viewer, "Test Viewer", roles, [new DepartmentMembershipDto(DepartmentId, "Leasing", IsPrimary: true)], IsGeynessStaff: false)),
                "/api/tickets/1" when request.Method == HttpMethod.Get => FakeApiHandler.JsonResponse(HttpStatusCode.OK, ticket),
                "/api/categories" => FakeApiHandler.JsonResponse(HttpStatusCode.OK, new[]
                {
                    new CategoryDto(11, "Handover", DepartmentId, "Leasing"),
                    new CategoryDto(12, "Other department", 99, "Elsewhere")
                }),
                "/api/request-types" => FakeApiHandler.JsonResponse(HttpStatusCode.OK, new[]
                {
                    new RequestTypeOptionDto(17, "Coordinate Handover", DepartmentId, 3, true),
                    new RequestTypeOptionDto(18, "Unpublished", DepartmentId, 3, false),
                    new RequestTypeOptionDto(19, "Other department", 99, 3, true)
                }),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            };
        });

        HttpClient Client() => new(handler) { BaseAddress = new Uri("http://localhost/") };
        var users = new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance);
        var model = new TicketDetailsModel(
            new TicketsApiClient(Client(), NullLogger<TicketsApiClient>.Instance),
            new TicketSlaApiClient(Client(), NullLogger<TicketSlaApiClient>.Instance),
            users,
            new TicketNameResolver(users, new DepartmentsApiClient(Client(), NullLogger<DepartmentsApiClient>.Instance)),
            new CategoriesApiClient(Client(), NullLogger<CategoriesApiClient>.Instance),
            new RequestTypesApiClient(Client(), NullLogger<RequestTypesApiClient>.Instance))
        {
            PageContext = new PageContext(new ActionContext(
                new DefaultHttpContext { User = Principal(roles) }, new RouteData(), new PageActionDescriptor()))
        };
        return (model, calls);
    }

    [Fact]
    public async Task AnUnclassifiedTicket_OffersClassify_WithOnlyItsOwnDepartmentsCategoriesAndPublishedRequestTypes()
    {
        var (model, calls) = Build(Ticket(categoryId: null), Roles.DepartmentEmployee);
        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.True(model.CanClassify);
        Assert.Equal([11], model.ClassifyCategories.Select(c => c.CategoryId));
        Assert.Equal([17], model.ClassifyRequestTypes.Select(r => r.RequestTypeId));
        Assert.Equal<byte>([1, 2, 3], model.ClassifyPriorities);              // current and more urgent only — lowering needs an approval
        Assert.Contains(calls, c => c.StartsWith("GET /api/categories?departmentId=4", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.Contains("handoff", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(Roles.ReportingUser)]
    [InlineData(Roles.ChairmanCeo)]
    public async Task AReadOnlyRole_IsNotOfferedClassify(string role)
    {
        var (model, calls) = Build(Ticket(categoryId: null), role);
        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.False(model.CanClassify);
        Assert.DoesNotContain(calls, c => c.StartsWith("GET /api/categories", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClassifiedOrClosedTicket_IsNotOfferedClassify()
    {
        var (classified, _) = Build(Ticket(categoryId: 5), Roles.DepartmentEmployee);
        await classified.OnGetAsync(1, null, CancellationToken.None);
        var (closed, _) = Build(Ticket(categoryId: null, status: "Closed"), Roles.DepartmentEmployee);
        await closed.OnGetAsync(1, null, CancellationToken.None);

        Assert.False(classified.CanClassify);
        Assert.False(closed.CanClassify);
    }

    [Fact]
    public async Task ATicketWhoseRequestTypeTheAiAlreadySet_ClassifiesWithoutSendingAnotherRequestType()
    {
        var (model, _) = Build(Ticket(categoryId: null, requestTypeId: 17), Roles.DepartmentEmployee);
        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.True(model.CanClassify);
        Assert.True(model.Classify.RequestTypeAlreadySet);
    }
}
