using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Reporting.Integration;

/// <summary>
/// The Team Performance endpoints through the real HTTP pipeline: the CS
/// Manager tier reads them, a CS Agent is refused, and the System
/// Administrator passes through the ADR-0024 override. Also the two
/// business refusals of the records endpoint (unknown metric, employee not
/// on the report), which are status codes — never exceptions.
/// </summary>
public class TeamPerformanceEndpointsTests(TigerCsApiFactory factory) : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory = factory;

    private async Task<(HttpClient Client, Guid EmployeeId)> ClientForAsync(string role)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    [Fact]
    public async Task CsAgent_IsRefused_OnBothEndpoints()
    {
        var (client, employeeId) = await ClientForAsync(Roles.CsAgent);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/reports/team-performance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/reports/team-performance/records?employeeId={employeeId}&metric=TicketsWorked")).StatusCode);
    }

    [Theory]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.ReportingUser)]
    public async Task OtherRoles_AreRefused(string role)
    {
        var (client, _) = await ClientForAsync(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/reports/team-performance")).StatusCode);
    }

    [Fact]
    public async Task CsManager_ReadsTheReport_WithEveryActiveCsAgentListed()
    {
        var (_, agentId) = await ClientForAsync(Roles.CsAgent);
        var (manager, managerId) = await ClientForAsync(Roles.CsManager);

        var response = await manager.GetAsync("/api/reports/team-performance?dateFrom=2026-08-01&dateTo=2026-09-01");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<TeamPerformanceReportDto>();
        Assert.NotNull(report);
        Assert.Equal(new DateOnly(2026, 8, 1), report!.Filters.DateFrom);
        Assert.Equal(new DateOnly(2026, 9, 1), report.Filters.DateTo);
        var agent = Assert.Single(report.Rows, r => r.EmployeeId == agentId);
        Assert.Equal(TeamPerformanceAgentTypes.CsAgent, agent.AgentType);
        // The manager is not an agent and has no row of their own.
        Assert.DoesNotContain(report.Rows, r => r.EmployeeId == managerId);
    }

    [Theory]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    public async Task GeneralManagerAndChairman_ReadTheReport(string role)
    {
        var (client, _) = await ClientForAsync(role);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reports/team-performance")).StatusCode);
    }

    /// <summary>Referenced by ProtectedEndpointInventoryTests: the System Administrator is authorized on both endpoints through the override.</summary>
    [Fact]
    public async Task SystemAdministrator_IsAuthorizedOnBothEndpoints_ThroughTheOverride()
    {
        var (_, agentId) = await ClientForAsync(Roles.CsAgent);
        var (admin, _) = await ClientForAsync(Roles.SystemAdministrator);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/reports/team-performance")).StatusCode);

        var records = await admin.GetAsync($"/api/reports/team-performance/records?employeeId={agentId}&metric=CurrentlyAssigned");
        Assert.Equal(HttpStatusCode.OK, records.StatusCode);
        var body = await records.Content.ReadFromJsonAsync<TeamPerformanceRecordsDto>();
        Assert.Equal(agentId, body!.EmployeeId);
        Assert.Equal("CurrentlyAssigned", body.Metric);
        Assert.Empty(body.Records);
    }

    [Fact]
    public async Task Records_RefuseAnUnknownMetricWith400_AndAnEmployeeNotOnTheReportWith404()
    {
        var (_, agentId) = await ClientForAsync(Roles.CsAgent);
        var (manager, managerId) = await ClientForAsync(Roles.CsManager);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await manager.GetAsync($"/api/reports/team-performance/records?employeeId={agentId}&metric=Velocity")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await manager.GetAsync($"/api/reports/team-performance/records?employeeId={managerId}&metric=TicketsWorked")).StatusCode);
    }
}
