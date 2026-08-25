using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// End-to-end against the real Api host: <c>GET /api/categories</c>, the
/// active-Category directory the New Ticket wizard's Category dropdown reads
/// from — removing the old "type a numeric CategoryId" temporary UI.
/// </summary>
public class CategoriesEndpointsTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public CategoriesEndpointsTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string role = "CS Agent")
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return client;
    }

    [Fact]
    public async Task GetCategories_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/categories");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCategories_NoDepartmentFilter_ReturnsEveryActiveCategoryAcrossDepartments()
    {
        var client = await CreateAuthenticatedClientAsync();
        var cs = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var fm = await _factory.CreateDepartmentAsync("Facilities Management " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var generalInquiry = await _factory.CreateCategoryAsync("General Inquiry", cs);
        var correctiveMaintenance = await _factory.CreateCategoryAsync("Corrective Maintenance", fm);
        await _factory.CreateCategoryAsync("Retired Category", fm, isActive: false);

        var response = await client.GetAsync("/api/categories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var categories = await response.Content.ReadFromJsonAsync<List<CategoryDto>>();
        var ids = categories!.Select(c => c.CategoryId).ToList();
        Assert.Contains(generalInquiry, ids);
        Assert.Contains(correctiveMaintenance, ids);
        Assert.DoesNotContain("Retired Category", categories!.Select(c => c.Name));
    }

    [Fact]
    public async Task GetCategories_DepartmentFilter_ReturnsOnlyThatDepartmentsActiveCategories()
    {
        var client = await CreateAuthenticatedClientAsync();
        var cs = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var fm = await _factory.CreateDepartmentAsync("Facilities Management " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var generalInquiry = await _factory.CreateCategoryAsync("General Inquiry", cs);
        await _factory.CreateCategoryAsync("Corrective Maintenance", fm);

        var response = await client.GetAsync($"/api/categories?departmentId={cs}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var categories = await response.Content.ReadFromJsonAsync<List<CategoryDto>>();
        var single = Assert.Single(categories!);
        Assert.Equal(generalInquiry, single.CategoryId);
        Assert.Equal(cs, single.DepartmentId);
    }

    [Fact]
    public async Task GetCategories_InactiveCategory_IsExcludedEvenWhenItsDepartmentIsFiltered()
    {
        var client = await CreateAuthenticatedClientAsync();
        var fm = await _factory.CreateDepartmentAsync("Facilities Management " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        await _factory.CreateCategoryAsync("Retired Category", fm, isActive: false);

        var response = await client.GetAsync($"/api/categories?departmentId={fm}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var categories = await response.Content.ReadFromJsonAsync<List<CategoryDto>>();
        Assert.Empty(categories!);
    }

    [Fact]
    public async Task GetCategories_UnknownDepartment_ReturnsEmptyListNot404()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/categories?departmentId=999999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var categories = await response.Content.ReadFromJsonAsync<List<CategoryDto>>();
        Assert.Empty(categories!);
    }

    [Fact]
    public async Task GetCategories_IncludesTheRoutedDepartmentsName()
    {
        var client = await CreateAuthenticatedClientAsync();
        var departmentName = "Facilities Management " + Guid.NewGuid();
        var fm = await _factory.CreateDepartmentAsync(departmentName, Guid.NewGuid().ToString("N")[..8]);
        await _factory.CreateCategoryAsync("Corrective Maintenance", fm);

        var response = await client.GetAsync($"/api/categories?departmentId={fm}");

        var categories = await response.Content.ReadFromJsonAsync<List<CategoryDto>>();
        Assert.Equal(departmentName, Assert.Single(categories!).DepartmentName);
    }

    // --- Server-side validation on POST /api/tickets: the dropdown is never trusted alone ---

    [Fact]
    public async Task CreateTicket_UnknownCategoryId_StillRejectedByTheApi()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500009999", null, false, null, null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.PostAsJsonAsync(
            "/api/tickets", new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, -2, 3, "x"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateTicket_InactiveCategory_IsRejected()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var fm = await _factory.CreateDepartmentAsync("Facilities Management " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var inactiveCategory = await _factory.CreateCategoryAsync("Retired Category", fm, isActive: false);
        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500009999", null, false, null, null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.PostAsJsonAsync(
            "/api/tickets", new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, inactiveCategory, 3, "x"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateTicket_CategoryFromADifferentDepartmentThanTheIntake_IsRejected()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var cs = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var fm = await _factory.CreateDepartmentAsync("Facilities Management " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var facilitiesCategory = await _factory.CreateCategoryAsync("Corrective Maintenance", fm);

        // The Intake explicitly named Customer Service, but the request carries a Facilities category.
        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500009999", cs, false, null, null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.PostAsJsonAsync(
            "/api/tickets", new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, facilitiesCategory, 3, "x"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("does not belong to the Intake department", problem!["detail"].ToString());
    }

    [Fact]
    public async Task CreateTicket_CategoryMatchingTheIntakesDepartment_Succeeds()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var fm = await _factory.CreateDepartmentAsync("Facilities Management " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var category = await _factory.CreateCategoryAsync("Corrective Maintenance", fm);
        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500009999", fm, false, null, null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.PostAsJsonAsync(
            "/api/tickets", new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, category, 3, "x"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateTicket_NoDepartmentOnIntake_AnyActiveCategorysDepartmentIsAccepted()
    {
        // The Intake named no Department at all — the dropdown offered every
        // active Category, and none of them can "mismatch" a Department the
        // Intake never named.
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var fm = await _factory.CreateDepartmentAsync("Facilities Management " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var category = await _factory.CreateCategoryAsync("Corrective Maintenance", fm);
        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500009999", null, false, null, null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.PostAsJsonAsync(
            "/api/tickets", new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, category, 3, "x"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
