using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using TigerCS.Api.OpenApi;

namespace TigerCS.Tests.OpenApi;

/// <summary>
/// Boots the Api once and parses <c>/swagger/v1/swagger.json</c>, so the
/// content assertions below share one host instead of starting a new one per
/// fact.
/// </summary>
public sealed class SwaggerDocumentFixture : IAsyncLifetime
{
    private SwaggerApiFactory _factory = null!;

    public OpenApiDocument Document { get; private set; } = null!;

    /// <summary>Every endpoint the running application actually exposes, as "METHOD path".</summary>
    public IReadOnlyCollection<string> ApiExplorerEndpoints { get; private set; } = [];

    public async Task InitializeAsync()
    {
        _factory = new SwaggerApiFactory("Development");
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync("/swagger/v1/swagger.json");
        Document = OpenApiModelFactory.Parse(json, "json").Document
            ?? throw new InvalidOperationException("The OpenAPI document failed to parse.");

        var descriptions = _factory.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
        ApiExplorerEndpoints = descriptions.ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Select(api => $"{api.HttpMethod?.ToUpperInvariant()} /{api.RelativePath}")
            .Distinct()
            .ToArray();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// What the generated OpenAPI document has to contain: every implemented
/// endpoint, the JWT bearer security definition behind Swagger UI's
/// <b>Authorize</b> button, and the documented tag groups.
/// </summary>
public class SwaggerDocumentTests(SwaggerDocumentFixture fixture) : IClassFixture<SwaggerDocumentFixture>
{
    /// <summary>
    /// Every endpoint implemented as of this change, as "METHOD path" with
    /// route constraints stripped (the form the OpenAPI document uses).
    /// Deliberately spelled out rather than derived, so adding an endpoint
    /// without documenting it fails here.
    /// </summary>
    public static readonly string[] ExpectedEndpoints =
    [
        "GET /health",

        "POST /api/auth/login",
        "POST /api/auth/logout",

        "GET /api/users/me",
        "PATCH /api/users/{employeeId}/activation",

        "GET /api/roles",

        "GET /api/departments",
        "GET /api/departments/{departmentId}/users",

        "GET /api/categories",

        "GET /api/crm/units/search",
        "GET /api/crm/units/{crmUnitId}",
        "GET /api/crm/units/{crmUnitId}/contacts",
        "GET /api/crm/buyers",

        "POST /api/verification-sessions",
        "GET /api/verification-sessions/{verificationSessionId}",

        "POST /api/intake-records",
        "GET /api/intake-records/{intakeRecordId}/customer-lookup",

        "POST /api/tickets",
        "GET /api/tickets",
        "GET /api/tickets/{ticketId}",
        "POST /api/tickets/{ticketId}/assignment",
        "POST /api/tickets/{ticketId}/transfer",
        "POST /api/tickets/{ticketId}/status",
        "POST /api/tickets/{ticketId}/resolution",
        "POST /api/tickets/{ticketId}/close",
        "POST /api/tickets/{ticketId}/reconciliation",
        "POST /api/tickets/{ticketId}/notes",
        "GET /api/tickets/{ticketId}/notes",
        "GET /api/tickets/{ticketId}/customer-history",
        "GET /api/tickets/{ticketId}/customer-profile",

        "GET /api/customers/crm/{crmCustomerId}/ticket-history",

        // SLA and Escalation (MVP-API-Contracts.md §5.1/§5.2/§5.7/§5.9).
        // Automatic Level 2 escalation on breach has no entry here on
        // purpose: §5.7 makes it system-triggered, so it is raised by the
        // background-job path and is not a client-callable endpoint.
        "GET /api/tickets/{ticketId}/sla",
        "POST /api/tickets/{ticketId}/sla/first-response",
        "POST /api/tickets/{ticketId}/escalations",
        "GET /api/tickets/{ticketId}/escalations"
    ];

    private IReadOnlyCollection<string> DocumentedEndpoints() =>
        fixture.Document.Paths
            .SelectMany(path => path.Value.Operations!
                .Select(operation => $"{operation.Key.ToString().ToUpperInvariant()} {path.Key}"))
            .ToArray();

    [Fact]
    public void Document_ContainsEveryImplementedEndpoint()
    {
        var documented = DocumentedEndpoints();

        Assert.Empty(ExpectedEndpoints.Except(documented).Order());
    }

    [Fact]
    public void Document_ContainsNoEndpointBeyondTheImplementedOnes()
    {
        var documented = DocumentedEndpoints();

        Assert.Empty(documented.Except(ExpectedEndpoints).Order());
    }

    /// <summary>
    /// The direct check behind "every implemented endpoint is documented":
    /// compares the document against what the running application's API
    /// explorer reports, rather than against a hand-written list.
    /// </summary>
    [Fact]
    public void Document_MatchesTheApplicationsOwnEndpointList()
    {
        var documented = DocumentedEndpoints();

        Assert.NotEmpty(fixture.ApiExplorerEndpoints);
        Assert.Empty(fixture.ApiExplorerEndpoints.Except(documented).Order());
    }

    [Fact]
    public void Document_DeclaresTheBearerJwtSecurityScheme()
    {
        var schemes = fixture.Document.Components?.SecuritySchemes;

        Assert.NotNull(schemes);
        Assert.True(schemes!.ContainsKey("Bearer"));

        // type: http + scheme: bearer is what makes Swagger UI send the pasted
        // token as `Authorization: Bearer {token}`.
        var bearer = Assert.IsType<OpenApiSecurityScheme>(schemes["Bearer"]);
        Assert.Equal(SecuritySchemeType.Http, bearer.Type);
        Assert.Equal("bearer", bearer.Scheme);
        Assert.Equal("JWT", bearer.BearerFormat);
        Assert.NotNull(bearer.Description);
    }

    [Theory]
    [InlineData("/api/tickets", "POST")]
    [InlineData("/api/users/me", "GET")]
    [InlineData("/api/roles", "GET")]
    public void AuthenticatedEndpoints_RequireTheBearerScheme(string path, string method)
    {
        var operation = OperationFor(path, method);

        Assert.NotNull(operation.Security);
        Assert.Contains(
            operation.Security!,
            requirement => requirement.Keys.Any(scheme => scheme.Reference?.Id == "Bearer"));
    }

    [Theory]
    [InlineData("/health", "GET")]
    [InlineData("/api/auth/login", "POST")]
    public void AnonymousEndpoints_DoNotRequireTheBearerScheme(string path, string method)
    {
        var operation = OperationFor(path, method);

        Assert.True(operation.Security is null || operation.Security.Count == 0);
    }

    [Theory]
    [InlineData("/api/tickets", "POST")]
    [InlineData("/api/users/me", "GET")]
    public void AuthenticatedEndpoints_Document401And403(string path, string method)
    {
        var operation = OperationFor(path, method);

        Assert.NotNull(operation.Responses);
        Assert.True(operation.Responses!.ContainsKey("401"));
        Assert.True(operation.Responses.ContainsKey("403"));
    }

    [Fact]
    public void Document_DeclaresEveryExpectedTag()
    {
        var declared = fixture.Document.Tags?.Select(tag => tag.Name).ToArray() ?? [];

        Assert.Equal(OpenApiTags.Ordered.Select(t => t.Name).ToArray(), declared);
    }

    [Theory]
    [InlineData("/health", "GET", OpenApiTags.Health)]
    [InlineData("/api/auth/login", "POST", OpenApiTags.Authentication)]
    [InlineData("/api/users/me", "GET", OpenApiTags.Users)]
    [InlineData("/api/roles", "GET", OpenApiTags.Roles)]
    [InlineData("/api/departments", "GET", OpenApiTags.Departments)]
    [InlineData("/api/departments/{departmentId}/users", "GET", OpenApiTags.Departments)]
    [InlineData("/api/categories", "GET", OpenApiTags.Categories)]
    [InlineData("/api/crm/units/search", "GET", OpenApiTags.CrmLookup)]
    [InlineData("/api/crm/buyers", "GET", OpenApiTags.CrmLookup)]
    [InlineData("/api/verification-sessions", "POST", OpenApiTags.CustomerVerification)]
    [InlineData("/api/intake-records", "POST", OpenApiTags.Intake)]
    [InlineData("/api/tickets", "POST", OpenApiTags.Tickets)]
    [InlineData("/api/tickets/{ticketId}/assignment", "POST", OpenApiTags.Assignment)]
    [InlineData("/api/tickets/{ticketId}/transfer", "POST", OpenApiTags.Transfer)]
    [InlineData("/api/tickets/{ticketId}/status", "POST", OpenApiTags.TicketLifecycle)]
    [InlineData("/api/tickets/{ticketId}/resolution", "POST", OpenApiTags.TicketLifecycle)]
    [InlineData("/api/tickets/{ticketId}/close", "POST", OpenApiTags.TicketLifecycle)]
    [InlineData("/api/tickets/{ticketId}/notes", "POST", OpenApiTags.Notes)]
    [InlineData("/api/tickets/{ticketId}/reconciliation", "POST", OpenApiTags.CrmReconciliation)]
    public void EndpointGroups_AreTaggedAsDocumented(string path, string method, string expectedTag)
    {
        var operation = OperationFor(path, method);

        Assert.NotNull(operation.Tags);
        Assert.Contains(operation.Tags!, tag => tag.Name == expectedTag);
    }

    [Fact]
    public void EveryOperation_CarriesExactlyOneKnownTag()
    {
        var known = OpenApiTags.Ordered.Select(t => t.Name).ToHashSet();

        foreach (var (path, item) in fixture.Document.Paths)
        {
            foreach (var (method, operation) in item.Operations!)
            {
                var tags = operation.Tags?.Select(t => t.Name).ToArray() ?? [];
                Assert.True(tags.Length == 1, $"{method} {path} should carry exactly one tag, but carries {tags.Length}.");
                Assert.True(known.Contains(tags[0]!), $"{method} {path} carries unknown tag '{tags[0]}'.");
            }
        }
    }

    [Fact]
    public void RequestBodies_AreDocumentedForWriteEndpoints()
    {
        var login = OperationFor("/api/auth/login", "POST");

        Assert.NotNull(login.RequestBody);
        Assert.True(login.RequestBody!.Content!.ContainsKey("application/json"));
    }

    [Fact]
    public void QueryParameters_AreDocumentedForTheTicketQueue()
    {
        var queue = OperationFor("/api/tickets", "GET");
        var names = (queue.Parameters ?? []).Select(p => p.Name).ToArray();

        // Paging.
        Assert.Contains("Page", names);
        Assert.Contains("PageSize", names);

        // Filtering.
        Assert.Contains("DepartmentId", names);
        Assert.Contains("CategoryId", names);
        Assert.Contains("PriorityId", names);
        Assert.Contains("TicketStatus", names);
        Assert.Contains("VerificationStatus", names);
        Assert.Contains("OwnerEmployeeId", names);
        Assert.Contains("Search", names);

        // Sorting.
        Assert.Contains("SortBy", names);
        Assert.Contains("SortDir", names);
    }

    [Fact]
    public void PagingParameters_AreDocumentedForTheDepartmentUserListing()
    {
        var listing = OperationFor("/api/departments/{departmentId}/users", "GET");
        var names = (listing.Parameters ?? []).Select(p => p.Name).ToArray();

        Assert.Contains("page", names);
        Assert.Contains("pageSize", names);
        Assert.Contains("activeOnly", names);
    }

    /// <summary>
    /// Every enum-backed field crosses the wire as a string or a byte (no
    /// domain enum type is exposed directly), so the accepted values cannot
    /// come from the generator — they are written into the field's
    /// description. This asserts they are actually there, for each domain
    /// enum a client has to supply or interpret.
    /// </summary>
    [Theory]
    [InlineData("CreateIntakeRecordRequestDto", "channelId", new[] { "Phone", "AppOrWebsite", "WhatsAppOrLiveChat", "SocialMediaDirectMessage", "FaceToFaceKiosk" })]
    [InlineData("IntakeRecordResponseDto", "crmVerificationStatus", new[] { "Unverified", "PendingCrmVerification", "Verified" })]
    [InlineData("CreateVerificationSessionRequestDto", "verificationMethod", new[] { "ManualAgentConfirmation", "AuthenticatedDigitalUser", "Otp", "FaceToFaceDocumentCheck", "Other" })]
    [InlineData("VerificationSessionResponseDto", "status", new[] { "InProgress", "Confirmed", "Consumed", "Expired", "Abandoned" })]
    [InlineData("ContactVerificationResponseDto", "contactType", new[] { "Owner", "Tenant", "Representative" })]
    [InlineData("TicketDetailDto", "ticketStatus", new[] { "Open", "InProgress", "PendingCustomer", "PendingThirdParty", "Resolved", "Closed" })]
    [InlineData("TicketDetailDto", "escalationLevel", new[] { "None", "Level1", "Level2", "Level3", "Level4" })]
    [InlineData("TicketDetailDto", "slaState", new[] { "Running", "Paused", "Met", "Breached", "NotApplicable" })]
    [InlineData("TicketDetailDto", "priorityId", new[] { "Critical", "High", "Medium", "Low" })]
    [InlineData("ResolveTicketRequestDto", "resolutionOutcome", new[] { "Resolved", "Cancelled", "Rejected", "Duplicate" })]
    public void EnumBackedFields_ListTheirAcceptedValues(string schemaName, string propertyName, string[] expectedValues)
    {
        var schemas = fixture.Document.Components?.Schemas;
        Assert.NotNull(schemas);
        Assert.True(schemas!.ContainsKey(schemaName), $"The document has no schema '{schemaName}'.");

        var properties = schemas[schemaName].Properties;
        Assert.NotNull(properties);
        Assert.True(properties!.ContainsKey(propertyName), $"'{schemaName}' has no property '{propertyName}'.");

        var description = properties[propertyName].Description ?? string.Empty;
        foreach (var value in expectedValues)
        {
            Assert.Contains(value, description, StringComparison.Ordinal);
        }
    }

    [Theory]
    // Nullable request fields the endpoints explicitly accept as absent.
    [InlineData("CreateIntakeRecordRequestDto", "rawUnitNumberEntered")]
    [InlineData("CreateIntakeRecordRequestDto", "priorityHint")]
    [InlineData("CreateTicketRequestDto", "unitReferenceId")]
    [InlineData("CreateTicketRequestDto", "contactReferenceId")]
    [InlineData("ActivationRequestDto", "reason")]
    [InlineData("ResolveTicketRequestDto", "reasonCode")]
    [InlineData("ResolveTicketRequestDto", "duplicateOfTicketId")]
    public void NullableFields_AreNotMarkedRequired(string schemaName, string propertyName)
    {
        var schema = SchemaFor(schemaName);

        Assert.True(schema.Properties!.ContainsKey(propertyName), $"'{schemaName}' has no property '{propertyName}'.");
        Assert.DoesNotContain(propertyName, schema.Required ?? new HashSet<string>());
    }

    [Theory]
    [InlineData("LoginRequestDto", "username")]
    [InlineData("LoginRequestDto", "password")]
    [InlineData("CreateIntakeRecordRequestDto", "channelId")]
    [InlineData("CreateIntakeRecordRequestDto", "phoneNumber")]
    [InlineData("CreateIntakeRecordRequestDto", "isUnitRelated")]
    [InlineData("AssignTicketRequestDto", "assignedEmployeeId")]
    [InlineData("AssignTicketRequestDto", "rowVersion")]
    [InlineData("ChangeStatusRequestDto", "newStatus")]
    [InlineData("TransferTicketRequestDto", "targetDepartmentId")]
    [InlineData("CreateTicketRequestDto", "categoryId")]
    public void MandatoryFields_AreMarkedRequired(string schemaName, string propertyName)
    {
        var schema = SchemaFor(schemaName);

        Assert.Contains(propertyName, schema.Required ?? new HashSet<string>());
    }

    [Theory]
    [InlineData("LoginRequestDto")]
    [InlineData("LoginResponseDto")]
    [InlineData("CreateIntakeRecordRequestDto")]
    [InlineData("TicketDetailDto")]
    [InlineData("TicketListResultDto")]
    public void Schemas_DescribeEveryProperty(string schemaName)
    {
        var schema = SchemaFor(schemaName);

        Assert.NotNull(schema.Properties);
        foreach (var (name, property) in schema.Properties!)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(property.Description),
                $"'{schemaName}.{name}' has no description in the OpenAPI document.");
        }
    }

    [Fact]
    public void Operations_CarryTheirSummaryFromTheXmlDocComments()
    {
        // Proves the XML documentation file is reaching the generator at all:
        // without it every summary would be null.
        foreach (var (path, item) in fixture.Document.Paths)
        {
            foreach (var (method, operation) in item.Operations!)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(operation.Summary),
                    $"{method} {path} has no summary in the OpenAPI document.");
            }
        }
    }

    private IOpenApiSchema SchemaFor(string schemaName)
    {
        var schemas = fixture.Document.Components?.Schemas;
        Assert.NotNull(schemas);
        Assert.True(schemas!.ContainsKey(schemaName), $"The document has no schema '{schemaName}'.");
        return schemas[schemaName];
    }

    private OpenApiOperation OperationFor(string path, string method)
    {
        Assert.True(fixture.Document.Paths.ContainsKey(path), $"The document has no path '{path}'.");
        var item = fixture.Document.Paths[path];

        var operation = item.Operations!
            .FirstOrDefault(o => string.Equals(o.Key.ToString(), method, StringComparison.OrdinalIgnoreCase));

        Assert.True(operation.Value is not null, $"The document has no {method} operation for '{path}'.");
        return operation.Value!;
    }
}
