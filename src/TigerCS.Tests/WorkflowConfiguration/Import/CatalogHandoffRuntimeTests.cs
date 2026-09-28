using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Import;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.WorkflowConfiguration.Import;

/// <summary>
/// What an imported handoff workflow does — and does not do — at runtime
/// while its request type does NOT enforce its configuration (the state
/// every imported type starts in; enforced behaviour is covered by
/// <c>ConfiguredRuntimeApiTests</c>),
/// end to end through the real Api (routing, authorization, application
/// services). The workflow VERSION and its steps' departments are stored
/// definitions: nothing reads them to move a ticket. The executable routing
/// is the existing Transfer action, authorized exactly as before (CS Manager
/// only) and narrowed only by the source department's transfer setting.
/// </summary>
public class CatalogHandoffRuntimeTests : IClassFixture<TigerCsApiFactory>
{
    private const string PhoneWithCrmMatch = "+971509990001";

    private readonly TigerCsApiFactory _factory;

    public CatalogHandoffRuntimeTests(TigerCsApiFactory factory) => _factory = factory;

    private sealed record Setup(int CustomerService, int Handover, int Collections, int RequestTypeId, int VersionId);

    /// <summary>
    /// Departments named as in the workbook, and one request type imported
    /// through the real importer from workbook row HO-NOC-001 with its
    /// unconfirmed Accounting leg and conditional approval taken out — the
    /// resulting flow is CS → Handover → back to CS, the one handoff shape
    /// whose target department exists today — and its SLA given in business
    /// hours, since the meaning of a business day is still undecided.
    /// </summary>
    private async Task<Setup> SetUpAsync()
    {
        await _factory.SeedPrioritiesAsync();
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        async Task<int> DepartmentAsync(string name)
        {
            var existing = await db.Departments.FirstOrDefaultAsync(d => d.Name == name);
            if (existing is not null)
            {
                return existing.DepartmentId;
            }

            var department = new Department(name, name[..3].ToUpperInvariant() + suffix);
            db.Departments.Add(department);
            await db.SaveChangesAsync();
            return department.DepartmentId;
        }

        var customerService = await DepartmentAsync(RequestTypeCatalogMapper.CustomerServiceDepartmentName);
        var handover = await DepartmentAsync("Handover");
        var collections = await DepartmentAsync("Collections");

        var workbookRow = RequestTypeCatalog.Load().Single(r => r.RequestCode == "HO-NOC-001");
        var row = workbookRow with
        {
            RequestCode = "DEMO-" + suffix,
            Name = "Handoff demo " + suffix,
            ProposedWorkflow = "CS Queue → CS Agent → Handover Agent → CS Agent → Resolve → Close",
            // In business hours: a business-day SLA is an open decision and
            // would keep the row from activating.
            ResolutionSla = "16 business hours",
            NeedsApproval = "No",
            ApprovalRole = null
        };

        var report = await RequestTypeCatalogImporter.ImportAsync(db, [row],
            new RequestTypeCatalogImportOptions(DateTime.UtcNow, Apply: true, ActivateResolved: true, AllowAgentPriorityChange: true));
        var result = Assert.Single(report.Results);
        Assert.Equal(RequestTypeImportOutcome.CreatedActive, result.Outcome);

        var requestType = await db.RequestTypes.SingleAsync(r => r.RequestTypeId == result.RequestTypeId);
        var version = await db.WorkflowTemplates.SingleAsync(t => t.WorkflowId == requestType.WorkflowId);

        // The stored definition names both departments.
        Assert.Equal(
            ["Ticket Created", "Customer Service Queue", "Customer Service Agent", "Handoff to Handover", "Handover Agent",
             "Return to Customer Service", "Customer Service Agent", "Resolve", "Close"],
            version.Steps.Select(s => s.Name));
        Assert.Equal(handover, version.Steps.Single(s => s.Name == "Handoff to Handover").DepartmentId);
        Assert.Equal(customerService, version.Steps.Single(s => s.Name == "Return to Customer Service").DepartmentId);

        return new Setup(customerService, handover, collections, requestType.RequestTypeId, version.WorkflowTemplateId);
    }

    private async Task<HttpClient> ClientAsync(string role, int? departmentId = null)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        if (departmentId is { } id)
        {
            await _factory.AssignPrimaryDepartmentAsync(employeeId, id);
        }

        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private async Task<long> CreateTicketAsync(HttpClient agent, Setup setup)
    {
        var categoryId = await _factory.CreateCategoryAsync("NOC " + Guid.NewGuid().ToString("N")[..6], setup.CustomerService);
        var intake = await (await agent.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithCrmMatch, null, true, "1204", null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        var lookup = await (await agent.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup"))
            .Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        var unit = Assert.Single(Assert.Single(lookup!.Sources.Single(s => s.Source == "Crm").Customers).Units);

        var response = await agent.PostAsJsonAsync("/api/tickets", new CreateTicketRequestDto(
            intake.IntakeRecordId, unit.UnitReferenceId, unit.ContactReferenceId, categoryId, (byte)PriorityLevel.Medium,
            "NOC for handover", RequestTypeId: setup.RequestTypeId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TicketResponseDto>())!.TicketId;
    }

    private static async Task<TicketDetailDto> GetAsync(HttpClient client, long ticketId) =>
        (await client.GetFromJsonAsync<TicketDetailDto>($"/api/tickets/{ticketId}"))!;

    private static async Task<HttpResponseMessage> TransferAsync(HttpClient client, long ticketId, int targetDepartmentId, string reason)
    {
        var current = await GetAsync(client, ticketId);
        return await client.PostAsJsonAsync($"/api/tickets/{ticketId}/transfer",
            new TransferTicketRequestDto(targetDepartmentId, reason, Convert.FromBase64String(current.RowVersion)));
    }

    [Fact]
    public async Task The_stored_handoff_steps_move_nothing_the_ticket_stays_where_it_was_created()
    {
        var setup = await SetUpAsync();
        var agent = await ClientAsync(Roles.CsAgent, setup.CustomerService);
        var ticketId = await CreateTicketAsync(agent, setup);

        var ticket = await GetAsync(await ClientAsync(Roles.CsManager), ticketId);

        // Pinned to the imported version, whose steps name Handover — yet the
        // ticket is, and stays, in Customer Service until someone transfers it.
        Assert.Equal(setup.VersionId, ticket.WorkflowTemplateId);
        Assert.Equal(setup.CustomerService, ticket.CurrentDepartmentId);
        Assert.DoesNotContain(await _factory.GetAuditEntriesAsync(ticketId.ToString()), e => e.Action == "Transfer");
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.DepartmentEmployee)]
    public async Task The_handoff_is_not_open_to_anyone_the_existing_Transfer_rule_excludes(string role)
    {
        var setup = await SetUpAsync();
        var ticketId = await CreateTicketAsync(await ClientAsync(Roles.CsAgent, setup.CustomerService), setup);
        var caller = await ClientAsync(role, setup.CustomerService);

        var response = await TransferAsync(caller, ticketId, setup.Handover, "Handoff to Handover");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(setup.CustomerService, (await GetAsync(await ClientAsync(Roles.CsManager), ticketId)).CurrentDepartmentId);
    }

    [Fact]
    public async Task A_CS_Manager_performs_the_handoff_and_the_return_to_Customer_Service_with_the_existing_Transfer_action()
    {
        var setup = await SetUpAsync();
        var ticketId = await CreateTicketAsync(await ClientAsync(Roles.CsAgent, setup.CustomerService), setup);
        var manager = await ClientAsync(Roles.CsManager);

        var handoff = await TransferAsync(manager, ticketId, setup.Handover, "Handoff to Handover");
        Assert.Equal(HttpStatusCode.OK, handoff.StatusCode);
        Assert.Equal(setup.Handover, (await GetAsync(manager, ticketId)).CurrentDepartmentId);

        var returned = await TransferAsync(manager, ticketId, setup.CustomerService, "Return to Customer Service");
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        var afterReturn = await GetAsync(manager, ticketId);
        Assert.Equal(setup.CustomerService, afterReturn.CurrentDepartmentId);
        Assert.Equal(setup.VersionId, afterReturn.WorkflowTemplateId);

        var transfers = (await _factory.GetAuditEntriesAsync(ticketId.ToString())).Where(e => e.Action == "Transfer").ToList();
        Assert.Equal(2, transfers.Count);
        Assert.Contains(transfers, t => t.AfterValue!.Contains($"DepartmentId={setup.Handover};"));
        Assert.Contains(transfers, t => t.AfterValue!.Contains($"DepartmentId={setup.CustomerService};"));
    }

    [Fact]
    public async Task The_stored_definition_does_not_restrict_routing_a_department_it_never_names_is_accepted_too()
    {
        var setup = await SetUpAsync();
        var ticketId = await CreateTicketAsync(await ClientAsync(Roles.CsAgent, setup.CustomerService), setup);
        var manager = await ClientAsync(Roles.CsManager);

        // Collections appears nowhere in the imported workflow.
        var response = await TransferAsync(manager, ticketId, setup.Collections, "Misrouted");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(setup.Collections, (await GetAsync(manager, ticketId)).CurrentDepartmentId);
    }

    [Fact]
    public async Task The_return_is_governed_by_the_existing_department_transfer_setting_not_by_the_definition()
    {
        var setup = await SetUpAsync();
        var ticketId = await CreateTicketAsync(await ClientAsync(Roles.CsAgent, setup.CustomerService), setup);
        var manager = await ClientAsync(Roles.CsManager);
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(manager, ticketId, setup.Handover, "Handoff to Handover")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var existing = await db.DepartmentWorkflowSettings.FirstOrDefaultAsync(s => s.DepartmentId == setup.Handover);
            if (existing is not null)
            {
                db.DepartmentWorkflowSettings.Remove(existing);
            }

            db.DepartmentWorkflowSettings.Add(new DepartmentWorkflowSettings(setup.Handover, true, true, allowTransferToOtherDepartments: false));
            await db.SaveChangesAsync();
        }

        try
        {
            // The definition says "Return to Customer Service"; the department
            // setting forbids transferring out of Handover — the setting wins.
            var response = await TransferAsync(manager, ticketId, setup.CustomerService, "Return to Customer Service");

            Assert.False(response.IsSuccessStatusCode);
            Assert.Contains("disabled-by-department-settings", await response.Content.ReadAsStringAsync());
            Assert.Equal(setup.Handover, (await GetAsync(manager, ticketId)).CurrentDepartmentId);
        }
        finally
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            db.DepartmentWorkflowSettings.Remove(await db.DepartmentWorkflowSettings.SingleAsync(s => s.DepartmentId == setup.Handover));
            await db.SaveChangesAsync();
        }
    }
}
