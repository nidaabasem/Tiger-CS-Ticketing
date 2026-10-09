using System.Text.Json;
using System.Text.RegularExpressions;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Dto;

namespace TigerCS.Tests.GenesysIntegration.Contracts;

/// <summary>
/// Contract tests for every Genesys data action in <c>docs/Genesys/data-actions</c>.
///
/// <para>
/// For each file the test renders <c>requestTemplate</c> (and the URL) with a
/// Velocity subset (<see cref="VelocityLite"/>) over plain ASCII, Arabic,
/// hostile (quotes, backslashes, newlines, markup, emoji), empty and missing
/// values, and checks that the body is valid JSON that the <b>real</b> request
/// DTO of the matching controller accepts and that the value survives the round
/// trip unchanged. It then serialises the <b>real</b> response DTO, applies the
/// <c>translationMap</c> JSONPaths and defaults, renders <c>successTemplate</c> and
/// checks it is valid JSON that matches the declared <c>successSchema</c>. Method,
/// path, query and headers are compared with the controller attributes by
/// reflection, and the declared inputs with the DTO's required members.
/// </para>
///
/// <para>
/// <b>Limit:</b> the Velocity and JSONPath behaviour is simulated from the
/// documented semantics - nothing here was run inside Genesys Cloud.
/// </para>
/// </summary>
public sealed partial class GenesysDataActionContractTests
{
    private const string PublicBase = "https://tigergroup.ae";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------
    //  What each file is supposed to be: route, request DTO, server-required inputs, real response samples.
    // ------------------------------------------------------------------

    private sealed record Spec(
        string Prefix,
        string Method,
        string Route,
        Type? RequestType,
        string[] ServerRequired,
        Func<IReadOnlyList<(string Label, object Sample)>> Responses);

    private static readonly Guid SessionId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static readonly GenesysTicketUpdateResponse Applied =
        new("Applied", "conv-1", 42, "TCK-2026-000042", "Open", false, 3, null, null);

    private static readonly Spec[] Specs =
    [
        new("01", "GET", "api/genesys/customers/lookup", null, ["phoneNumber"], () =>
        [
            ("found", new GenesysCustomerLookupResultDto("+971501234567", true, "Found", [], [], [], 1,
                new GenesysScreenPopDto("محمد \"أحمد\" O'Brien", "a@b.example", "Crm", "1001", 1, ["Tower 1 / 1205"], "Tower 1 / 1205", ["TCK-1"], ["TCK-1"], "TCK-1 (Open)"))),
            ("notFound", new GenesysCustomerLookupResultDto("+971501234567", false, "NotFound", [], [], [], 0,
                new GenesysScreenPopDto("", "", "", "", 0, [], "", [], [], "")))
        ]),
        new("02", "POST", "api/genesys/tickets", typeof(GenesysInquiryRequest), ["conversationId", "channel"], () =>
        [
            ("created", new GenesysInquiryAcceptedResponse("TicketCreated", "conv-1", 42, "TCK-2026-000042"))
        ]),
        new("03", "PATCH", "api/genesys/tickets/{ticketId}", typeof(GenesysTicketUpdateRequest), ["ticketId", "conversationId"], () => [("applied", Applied)]),
        new("04", "PATCH", "api/genesys/tickets/{ticketId}", typeof(GenesysTicketUpdateRequest), ["ticketId", "conversationId"], () =>
            [("ended", Applied with { ConversationEnded = true })]),
        new("05", "PATCH", "api/genesys/tickets/{ticketId}", typeof(GenesysTicketUpdateRequest), ["ticketId", "conversationId"], () =>
            [("waiting", Applied with { HandoffStatus = "WaitingForAgent", TicketAgentHandoffId = 7 })]),
        new("06", "PATCH", "api/genesys/tickets/{ticketId}", typeof(GenesysTicketUpdateRequest), ["ticketId", "conversationId", "reason"], () => [("applied", Applied)]),
        new("07", "PATCH", "api/genesys/tickets/{ticketId}", typeof(GenesysTicketUpdateRequest), ["ticketId", "conversationId"], () => [("applied", Applied)]),
        new("08", "GET", "api/genesys/collections/customers/by-key/{customerKey}/payment-summary", null, ["customerKey"], () =>
        [
            ("full", PaymentSummary(full: true)),
            ("noFigures", PaymentSummary(full: false))
        ]),
        new("09", "GET", "api/genesys/collections/customers/by-key/{customerKey}/payment-transactions", null, ["customerKey", "companyId", "type"], () =>
        [
            ("items", PaymentTransactions(
                new CollectionsEdsmTransactionDto(1500m, "Formatted", "1,500.00", new DateOnly(2026, 9, 1), "01/09/2026", "CHQ-1", "Cheque", 1),
                new CollectionsEdsmTransactionDto(null, "Formatted", null, null, null, null, null))),
            ("none", PaymentTransactions())
        ]),
        new("10", "PATCH", "api/genesys/tickets/{ticketId}", typeof(GenesysTicketUpdateRequest), ["ticketId", "conversationId", "awaiting"], () =>
        [
            ("running", Applied with { AwaitingCustomerReply = true, InactivityDeadlineUtc = new DateTime(2026, 10, 8, 10, 5, 0, DateTimeKind.Utc) }),
            ("notStarted", Applied with { AwaitingCustomerReply = false, AwaitingCustomerReplyNote = "A human follow-up is pending." })
        ]),
        new("11", "POST", "api/genesys/documents/send-copy", typeof(CrmDocumentCopyRequestDto), ["verificationSessionId", "documentType", "idempotencyKey"], () =>
        [
            ("sent", new CrmDocumentCopyResult(CrmDocumentCopyStatus.Sent, RecordId: "5001", MaskedDestination: "a***@g***.com", DeliveryChannel: "Email", Duplicate: true)),
            ("selection", new CrmDocumentCopyResult(CrmDocumentCopyStatus.SelectionRequired, Code: "SELECTION_REQUIRED", Message: "Which one?",
                Choices: [new CrmDocumentChoice("5001", "Contract \"A\"", "1205", new DateTime(2026, 1, 2)), new CrmDocumentChoice("5002", "عقد", null, null)], ChoiceKind: "Document")),
            ("queued", new CrmDocumentCopyResult(CrmDocumentCopyStatus.Queued))
        ]),
        new("12", "POST", "api/genesys/customers/unit-details", typeof(GenesysCustomerUnitDetailsRequest), ["customerReference", "phoneNumber"], () =>
        [
            ("select", new GenesysCustomerUnitDetailsResponse("UnitSelectionRequired", "crm:1001",
                [new GenesysEligibleUnitDto(41230, "1205", 79, "Tiger Tower", 12, "Sold")], null, null, null, null)),
            ("details", new GenesysCustomerUnitDetailsResponse("UnitDetails", "crm:1001", [],
                new GenesysUnitDetailsDto(41230, "1205", "Tower A", 12, new GenesysUnitTypeDto(2, "Apartment"), 2, new GenesysAreaDto(1250.5m, "sqft"),
                    new GenesysBookingDto("BK-1", "Sold", 8), [], "2027-06-30", "2027-07-15"),
                new GenesysProjectDetailsDto(79, "Tiger Tower", "برج", "Dubai", "Under construction", "2027-03-31", "2027-04-01", null, null, 62.5m, "2027-01-31", "2027-02-10"),
                "Unit", "Available",
                new GenesysSaleDto(new GenesysMoneyDto(1850000.5m, "AED"), new GenesysMoneyDto(74000m, "AED")), "Available")),
            // CRM recorded nothing: every optional member is null - the data action must not turn that into 0.
            ("nullsEverywhere", new GenesysCustomerUnitDetailsResponse("UnitDetails", "crm:1001", [],
                new GenesysUnitDetailsDto(41230, null, null, null, new GenesysUnitTypeDto(2, null), null, null, new GenesysBookingDto("BK-1", null, 8), null, null, null),
                new GenesysProjectDetailsDto(79, null, null, null, null, null, null, null, null),
                null, "NotAvailable"))
        ]),
        new("13", "POST", "api/genesys/verification/buyer-lookup", typeof(BuyerLookupRequestDto), ["phoneNumber"], () =>
        [
            ("found", new CustomerOtpResult(CustomerOtpStatus.Found, Units: [new BuyerUnitChoice("41230", 12345, "1205", "Tiger \"Sky\" Tower")], MaskedDestination: "a***@g***.com",
                MaskedMobile: "+971******888", AvailableChannels: ["Email", "Sms"]))
        ]),
        new("14", "POST", "api/genesys/verification/otp/send", typeof(OtpSendRequestDto), ["phoneNumber"], () =>
        [
            ("sent", new CustomerOtpResult(CustomerOtpStatus.CodeSent, ChallengeId: SessionId, MaskedDestination: "a***@g***.com", ExpiresAtUtc: DateTime.UtcNow, Channel: "Email")),
            ("sentBySms", new CustomerOtpResult(CustomerOtpStatus.CodeSent, ChallengeId: SessionId, MaskedDestination: "+971******888", ExpiresAtUtc: DateTime.UtcNow, Channel: "Sms")),
            ("unconfirmed", new CustomerOtpResult(CustomerOtpStatus.DeliveryUnconfirmed, Code: "OTP_DELIVERY_UNCONFIRMED", Message: "Unknown", ChallengeId: SessionId, MaskedDestination: "+971******888", Channel: "Sms")),
            ("selection", new CustomerOtpResult(CustomerOtpStatus.UnitSelectionRequired, Code: "UNIT_SELECTION_REQUIRED", Message: "Choose",
                Units: [new BuyerUnitChoice("41230", 12345, "1205", null), new BuyerUnitChoice("41231", 12346, "1403", "P")]))
        ]),
        new("15", "POST", "api/genesys/verification/otp/resend", typeof(OtpResendRequestDto), ["challengeId"], () =>
        [
            ("sent", new CustomerOtpResult(CustomerOtpStatus.CodeSent, ChallengeId: SessionId, MaskedDestination: "a***@g***.com", Channel: "Email")),
            ("sentBySms", new CustomerOtpResult(CustomerOtpStatus.CodeSent, ChallengeId: SessionId, MaskedDestination: "+971******888", Channel: "Sms"))
        ]),
        new("16", "POST", "api/genesys/verification/otp/verify", typeof(OtpVerifyRequestDto), ["challengeId", "code"], () =>
        [
            ("verified", new CustomerOtpResult(CustomerOtpStatus.Verified, Session: new VerificationSessionResponseDto(
                SessionId, Guid.NewGuid(), 1, 2, "Confirmed", true, "Otp", DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(30),
                "1205", "Tiger Tower", "Tower A", "Apartment", "Ahmed", "email")))
        ])
    ];

    private static CollectionsPaymentSummaryResponseDto PaymentSummary(bool full)
    {
        var available = new CollectionsCompanyPaymentSummaryDto(
            4, "Tiger Properties", "Sales", "Available", null, [],
            [
                new CollectionsEdsmFieldDto("paidAmount", "Paid", "d", "Available", 100m, "100.00", null),
                new CollectionsEdsmFieldDto("dueAmount", "Due", "d", "Available", 20m, "20.00", null),
                new CollectionsEdsmFieldDto("outstandingAmount", "Outstanding", "d", "Available", 50m, "50.00", null),
                new CollectionsEdsmFieldDto("totalAmount", "Total", "d", "Available", 170m, "170.00", null)
            ],
            "Ok", false, [], [], null);
        var unavailable = available with { CompanyId = 5, CompanyName = null, BusinessModel = null, Status = "Unavailable", StatusDetail = "EDSM down", Fields = [] };
        var next = new CollectionsEdsmNextPaymentDto(
            "Available", [], null, true, new DateOnly(2026, 10, 8), new DateOnly(2027, 4, 8),
            new CollectionsNextPaymentItemDto(4, 3001, new DateOnly(2026, 11, 1), 500m, "500.00", "AED", []), []);

        return new CollectionsPaymentSummaryResponseDto(
            "ext:Pact:3001", full ? "Mapped" : "NotMapped", null, "3001", "EDSM", DateTime.UtcNow, DateTime.UtcNow, "AED", "Config", null, 5, 20,
            DateTime.UtcNow, "Pact", full ? [available, unavailable] : [], [],
            full ? CollectionsCompleteness.Partial : CollectionsCompleteness.NoFigures,
            full ? ["Company 5 unavailable"] : null,
            full ? next : null);
    }

    private static CollectionsPaymentTransactionsResponseDto PaymentTransactions(params CollectionsEdsmTransactionDto[] items) =>
        new("ext:Pact:3001", "3001", 4, "Tiger Properties", "Sales", "Paid", 1, null, "AED", "Config", "EDSM", DateTime.UtcNow, null, 20,
            DateTime.UtcNow, "Pact", items);

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(DataActionsDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    private static string DataActionsDirectory => Path.Combine(ApiRouteCatalog.RepoRoot(), "docs", "Genesys", "data-actions");

    private static JsonElement Load(string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(DataActionsDirectory, fileName))).RootElement.Clone();

    private static Spec SpecFor(string fileName) => Specs.Single(s => fileName.StartsWith(s.Prefix + "-", StringComparison.Ordinal));

    // ------------------------------------------------------------------
    //  Inventory
    // ------------------------------------------------------------------

    [Fact]
    public void FilesAreNumberedUniquelyAndSequentially_00To16_AndEveryOneIsValidJson()
    {
        var files = Directory.GetFiles(DataActionsDirectory, "*.json").Select(Path.GetFileName).Select(f => f!).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var numbers = files.Select(f => Regex.Match(f, @"^(\d{2})-[a-z0-9-]+\.json$")).ToList();

        Assert.All(numbers, m => Assert.True(m.Success, "File name must be NN-kebab-name.json"));
        Assert.Equal(Enumerable.Range(0, files.Count).Select(n => n.ToString("00")), numbers.Select(m => m.Groups[1].Value));
        Assert.Equal(17, files.Count); // 00..16: a new action must update this test, the inventory and the route table
        Assert.Equal(Specs.Select(s => s.Prefix).Order(), files.Skip(1).Select(f => f[..2]).Order());

        var names = new List<string>();
        foreach (var file in files)
        {
            var root = Load(file); // throws on invalid JSON
            if (file.StartsWith("00-", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Equal("custom-rest-actions", root.GetProperty("integrationType").GetString());
            Assert.Equal("custom", root.GetProperty("actionType").GetString());
            Assert.Equal("TigerCS", root.GetProperty("category").GetString());
            names.Add(root.GetProperty("name").GetString()!);
        }

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, n => Assert.StartsWith("TigerCS - ", n));
    }

    [Fact]
    public void TheCustomAuthFile_PostsTheDocumentedClientCredentialsGrant_ToThePublicBase()
    {
        var request = Load("00-custom-auth-request-config.json").GetProperty("config").GetProperty("request");

        Assert.Equal("POST", request.GetProperty("requestType").GetString());
        Assert.Equal(PublicBase + "/api/genesys/oauth/token", request.GetProperty("requestUrlTemplate").GetString());
        var body = VelocityLite.Render(request.GetProperty("requestTemplate").GetString()!, Context(new() { }, ("credentials", new Dictionary<string, object?> { ["clientId"] = "a b&c", ["clientSecret"] = "s=1" })));
        Assert.Equal("grant_type=client_credentials&scope=ticketing.genesys&client_id=a%20b%26c&client_secret=s%3D1", body);
    }

    // ------------------------------------------------------------------
    //  Request side
    // ------------------------------------------------------------------

    [Theory, MemberData(nameof(Files))]
    public void Route_MethodPathQueryAndHeaders_MatchTheControllerAttributes(string file)
    {
        if (file.StartsWith("00-", StringComparison.Ordinal)) return;
        var spec = SpecFor(file);
        var request = Load(file).GetProperty("config").GetProperty("request");

        Assert.Equal(spec.Method, request.GetProperty("requestType").GetString());

        var url = request.GetProperty("requestUrlTemplate").GetString()!;
        Assert.StartsWith(PublicBase + "/", url);
        var pathAndQuery = url[PublicBase.Length..];
        var path = pathAndQuery.Split('?')[0].TrimStart('/');
        var normalised = InputReference().Replace(path, "{$1}");
        Assert.Equal(spec.Route, normalised);

        var route = ApiRouteCatalog.GenesysRoutes.SingleOrDefault(r => r.Method == spec.Method && r.Route == normalised);
        Assert.True(route is not null, $"{file}: no controller declares {spec.Method} /{normalised}");

        if (pathAndQuery.Contains('?'))
        {
            foreach (var pair in pathAndQuery.Split('?', 2)[1].Split('&'))
            {
                var key = pair.Split('=')[0];
                Assert.True(route!.QueryParameters.Contains(key), $"{file}: query parameter '{key}' is not bound by {route.Action.Name}");
            }
        }

        var headers = request.GetProperty("headers").EnumerateObject().Select(h => h.Name).ToList();
        foreach (var header in route!.Headers)
        {
            Assert.Contains(header, headers, StringComparer.OrdinalIgnoreCase); // e.g. Idempotency-Key must be sent
        }

        var proxyOnly = new[] { "Authorization", "Content-Type", "Accept", "X-Genesys-Flow-Timeout-Seconds" };
        foreach (var header in headers.Where(h => !proxyOnly.Contains(h, StringComparer.OrdinalIgnoreCase)))
        {
            Assert.True(route.Headers.Contains(header), $"{file}: header '{header}' is not read by {route.Action.Name}");
        }

        Assert.Equal("${authResponse.token_type} ${authResponse.access_token}", request.GetProperty("headers").GetProperty("Authorization").GetString());
        if (spec.Method != "GET")
        {
            Assert.Equal("application/json", request.GetProperty("headers").GetProperty("Content-Type").GetString());
        }
    }

    [Theory, MemberData(nameof(Files))]
    public void Inputs_AreDeclared_RequiredOnesMatchTheServer_AndEveryInputIsUsed(string file)
    {
        if (file.StartsWith("00-", StringComparison.Ordinal)) return;
        var spec = SpecFor(file);
        var root = Load(file);
        var schema = root.GetProperty("contract").GetProperty("input").GetProperty("inputSchema");
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var required = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(x => x.GetString()!).ToHashSet() : [];

        // What the server insists on: the file's list above plus every non-nullable, no-default constructor member of the DTO.
        var expected = spec.ServerRequired.ToHashSet(StringComparer.Ordinal);
        if (spec.RequestType is not null)
        {
            expected.UnionWith(RequiredMembers(spec.RequestType));
        }

        Assert.True(expected.IsSubsetOf(required), $"{file}: schema.required is missing {string.Join(", ", expected.Except(required))}");
        Assert.True(required.IsSubsetOf(properties), $"{file}: required but not declared: {string.Join(", ", required.Except(properties))}");

        var config = root.GetProperty("config").GetProperty("request");
        var text = config.GetRawText();
        var referenced = InputUsage().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        referenced.Remove("rawRequest"); // Genesys' own passthrough on GET actions
        Assert.True(referenced.IsSubsetOf(properties), $"{file}: used but not declared in inputSchema: {string.Join(", ", referenced.Except(properties))}");
        Assert.True(properties.IsSubsetOf(referenced), $"{file}: declared but never used: {string.Join(", ", properties.Except(referenced))}");
        Assert.True(required.IsSubsetOf(referenced), $"{file}: required input is never sent: {string.Join(", ", required.Except(referenced))}");
    }

    public static TheoryData<string> BodyFiles()
    {
        var data = new TheoryData<string>();
        foreach (var spec in Specs.Where(s => s.Method != "GET"))
        {
            data.Add(Directory.GetFiles(DataActionsDirectory, spec.Prefix + "-*.json").Select(Path.GetFileName).Single()!);
        }

        return data;
    }

    [Theory, MemberData(nameof(BodyFiles))]
    public void RequestBody_IsValidJson_AndDeserialisesIntoTheRealDto_ForEveryKindOfValue(string file)
    {
        var spec = SpecFor(file);
        var root = Load(file);
        var template = root.GetProperty("config").GetProperty("request").GetProperty("requestTemplate").GetString()!;
        var schemaProperties = root.GetProperty("contract").GetProperty("input").GetProperty("inputSchema").GetProperty("properties");
        var required = root.GetProperty("contract").GetProperty("input").GetProperty("inputSchema").TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(x => x.GetString()!).ToHashSet() : [];

        var baseline = Baseline(schemaProperties);
        var inBody = InputUsage().Matches(template).Select(m => m.Groups[1].Value).ToHashSet();

        // The all-baseline body deserialises.
        AssertBody(template, baseline, spec.RequestType!, file);

        foreach (var (name, _) in baseline.Where(kv => inBody.Contains(kv.Key)))
        {
            var isString = schemaProperties.GetProperty(name).GetProperty("type").GetString() == "string";
            var isFreeText = isString && !TypedStrings.Contains(name);

            // Genesys enforces integer/boolean input types, so only strings can carry hostile text.
            var values = isString ? HostileValues : HostileValues.Where(v => v.Value is null).ToArray();
            foreach (var (label, value) in values)
            {
                if (value is null && required.Contains(name))
                {
                    continue; // a required input is always supplied by the flow; a missing one is the flow's bug, not the template's
                }

                var input = new Dictionary<string, object?>(baseline) { [name] = value };
                var body = Render(template, input);
                using var json = ParseOrFail(body, file, name, label); // valid JSON for every value, typed or not

                if (!isFreeText)
                {
                    continue; // a hostile GUID/date is legitimately rejected by the DTO
                }

                var dto = JsonSerializer.Deserialize(body, spec.RequestType!, Web);
                Assert.NotNull(dto);

                if (value is string text && text.Length > 0)
                {
                    Assert.True(
                        StringLeaves(json.RootElement).Contains(text),
                        $"{file}: '{name}' ({label}) did not survive unchanged in the body: {body}");
                }
            }
        }
    }

    [Theory, MemberData(nameof(Files))]
    public void UrlAndHeaders_CannotBeSteeredOffThePath_ByHostileInput(string file)
    {
        if (file.StartsWith("00-", StringComparison.Ordinal)) return;
        var root = Load(file);
        var request = root.GetProperty("config").GetProperty("request");
        var properties = root.GetProperty("contract").GetProperty("input").GetProperty("inputSchema").GetProperty("properties");
        var template = request.GetProperty("requestUrlTemplate").GetString()!;
        var baseline = Baseline(properties);
        var baselineUrl = Render(template, baseline);
        var inUrl = InputUsage().Matches(template).Select(m => m.Groups[1].Value).ToHashSet();

        Assert.DoesNotContain("$", baselineUrl);
        Assert.DoesNotContain("{", baselineUrl);

        foreach (var name in inUrl.Where(n => properties.GetProperty(n).GetProperty("type").GetString() == "string"))
        {
            foreach (var (label, value) in HostileValues.Where(v => v.Value is string { Length: > 0 }))
            {
                var url = Render(template, new Dictionary<string, object?>(baseline) { [name] = value });
                var uri = new Uri(url);
                var expectedSegments = new Uri(baselineUrl).AbsolutePath.Split('/').Length;
                Assert.True(
                    uri.AbsolutePath.Split('/').Length == expectedSegments && url.Count(c => c == '?') == baselineUrl.Count(c => c == '?') && !url.Contains('#') && !url.Contains(' '),
                    $"{file}: '{name}' ({label}) changes the request path/query: {url}");
            }
        }

        foreach (var header in request.GetProperty("headers").EnumerateObject())
        {
            // A header value must never carry a line break, whatever the flow passes.
            foreach (var (_, value) in HostileValues.Where(v => v.Value is string))
            {
                var input = baseline.ToDictionary(kv => kv.Key, kv => kv.Value is string ? value : kv.Value);
                var rendered = Render(header.Value.GetString()!, input);
                if (header.Name is "Idempotency-Key" or "X-Genesys-Flow-Timeout-Seconds")
                {
                    continue; // flow-controlled free text; the proxy validates (documented in the forwarding rules)
                }

                Assert.DoesNotContain('\n', rendered);
            }
        }
    }

    // ------------------------------------------------------------------
    //  Response side
    // ------------------------------------------------------------------

    [Theory, MemberData(nameof(Files))]
    public void SuccessTemplate_RendersValidJson_ForEveryRealResponse_AndMatchesTheDeclaredSchema(string file)
    {
        if (file.StartsWith("00-", StringComparison.Ordinal)) return;
        var spec = SpecFor(file);
        var root = Load(file);
        var response = root.GetProperty("config").GetProperty("response");
        var allowedKeys = new[] { "translationMap", "translationMapDefaults", "successTemplate", "errorTemplates" };
        Assert.All(response.EnumerateObject(), p => Assert.Contains(p.Name, allowedKeys));

        var map = response.GetProperty("translationMap").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var defaults = response.GetProperty("translationMapDefaults").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var template = response.GetProperty("successTemplate").GetString()!;
        var schema = root.GetProperty("contract").GetProperty("output").GetProperty("successSchema").GetProperty("properties");

        Assert.True(defaults.Keys.All(map.ContainsKey), $"{file}: defaults for unmapped names: {string.Join(", ", defaults.Keys.Except(map.Keys))}");
        foreach (var d in defaults)
        {
            using var parsed = ParseOrFail(d.Value, file, "default:" + d.Key, "default"); // a default must itself be a JSON value
            if (schema.TryGetProperty(d.Key, out var declaredType) && declaredType.GetProperty("type").GetString() == "string")
            {
                // A string default is "" (or a word) - never a string that itself contains quote characters.
                Assert.True(
                    parsed.RootElement.ValueKind == JsonValueKind.String && !parsed.RootElement.GetString()!.Contains('"') && !parsed.RootElement.GetString()!.Contains('\\'),
                    $"{file}: default for '{d.Key}' is {d.Value}");
            }
        }

        var used = Regex.Matches(template, @"\$\{(\w+)\}").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(used.IsSubsetOf(map.Keys), $"{file}: successTemplate uses undefined ${{{string.Join("}, ${", used.Except(map.Keys))}}}");

        var samples = spec.Responses().Select(s => (s.Label, Json: JsonSerializer.Serialize(s.Sample, Web))).ToList();
        samples.Add(("emptyObject", "{}")); // everything missing: only the defaults apply

        foreach (var (label, json) in samples)
        {
            using var doc = JsonDocument.Parse(json);
            var variables = new Dictionary<string, object?>();
            foreach (var (name, path) in map)
            {
                var value = MiniJsonPath.Evaluate(doc.RootElement, path) ?? defaults.GetValueOrDefault(name);
                if (label != "emptyObject" && !defaults.ContainsKey(name))
                {
                    Assert.True(value is not null, $"{file}/{label}: '{name}' ({path}) matched nothing in the real response: {json}");
                }

                variables[name] = value;
            }

            var rendered = VelocityLite.Render(template, variables);
            if (label == "emptyObject" && map.Keys.Any(k => !defaults.ContainsKey(k)))
            {
                continue; // names without a default cannot render from nothing; reported by the other samples
            }

            using var output = ParseOrFail(rendered, file, label, "successTemplate");
            AssertConformsToSchema(output.RootElement, schema, $"{file}/{label}");

            if (file.StartsWith("12-", StringComparison.Ordinal) && label == "nullsEverywhere")
            {
                // CRM recorded nothing: the bot must see a sentinel it can recognise, never a plausible 0.
                Assert.Equal(-999, output.RootElement.GetProperty("floor").GetInt32());
                Assert.Equal(-1, output.RootElement.GetProperty("bedrooms").GetInt32());
                Assert.Equal(-1, output.RootElement.GetProperty("areaValue").GetInt32());
                Assert.Equal("NotAvailable", output.RootElement.GetProperty("detailsStatus").GetString());
            }
        }
    }

    [Theory, MemberData(nameof(Files))]
    public void EveryMappedPath_ExistsInTheRealResponseDto(string file)
    {
        if (file.StartsWith("00-", StringComparison.Ordinal)) return;
        var spec = SpecFor(file);
        var map = Load(file).GetProperty("config").GetProperty("response").GetProperty("translationMap").EnumerateObject().ToList();

        // Union of all sample shapes: every mapped path must resolve to something in at least one (null-valued members count as present).
        var documents = spec.Responses().Select(s => JsonDocument.Parse(JsonSerializer.Serialize(s.Sample, Web))).ToList();
        foreach (var entry in map)
        {
            var path = entry.Value.GetString()!;
            var prefix = Regex.Match(path, @"^\$(\.\w+)+").Value; // the plain property chain before any [*] / filter / length()
            if (path.Contains(".length()", StringComparison.Ordinal))
            {
                prefix = prefix.Replace(".length", string.Empty, StringComparison.Ordinal);
            }

            var found = documents.Any(d => PropertyChainExists(d.RootElement, prefix));
            Assert.True(found, $"{file}: '{entry.Name}' maps {path}, but the response DTO has no such member.");
        }
    }

    /// <summary>A journey only works if what one action returns is something the next one accepts as input.</summary>
    [Theory]
    [InlineData("02", "ticketId", "03")]
    [InlineData("02", "ticketId", "04")]
    [InlineData("02", "ticketId", "05")]
    [InlineData("02", "ticketId", "06")]
    [InlineData("02", "ticketId", "07")]
    [InlineData("13", "units", "14")]
    [InlineData("14", "challengeId", "15")]
    [InlineData("14", "challengeId", "16")]
    [InlineData("15", "challengeId", "16")]
    [InlineData("16", "verificationSessionId", "11")]
    [InlineData("11", "recordId", "11")]
    [InlineData("01", "externalCustomerId", "12")]
    [InlineData("01", "externalCustomerId", "08")]
    [InlineData("08", "availableCompanyIds", "09")]
    [InlineData("12", "eligibleUnits", "12")]
    public void JourneyHandOffs_ProducedByOneAction_AreDeclaredInputsOfTheNext(string from, string output, string to)
    {
        var producer = Load(Directory.GetFiles(DataActionsDirectory, from + "-*.json").Select(Path.GetFileName).Single()!);
        var consumer = Load(Directory.GetFiles(DataActionsDirectory, to + "-*.json").Select(Path.GetFileName).Single()!);
        var produced = producer.GetProperty("contract").GetProperty("output").GetProperty("successSchema").GetProperty("properties");
        var inputs = consumer.GetProperty("contract").GetProperty("input").GetProperty("inputSchema").GetProperty("properties");

        Assert.True(produced.TryGetProperty(output, out _), $"{from} does not declare output '{output}'");

        // The consuming input name: same name, or the documented renames (OTP units pick -> crmUnitId, id -> customerReference/customerKey, list -> companyId/unitId).
        var consumed = (from, output, to) switch
        {
            ("13", "units", "14") => "crmUnitId",
            ("01", "externalCustomerId", "12") => "customerReference",
            ("01", "externalCustomerId", "08") => "customerKey",
            ("08", "availableCompanyIds", "09") => "companyId",
            ("12", "eligibleUnits", "12") => "unitId",
            _ => output
        };
        Assert.True(inputs.TryGetProperty(consumed, out _), $"{to} does not declare input '{consumed}' for the value {from} returns as '{output}'");
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static readonly (string Label, string? Value)[] HostileValues =
    [
        ("ascii", "ABC-123 plain"),
        ("arabic", "محمد أحمد"),
        ("hostile", "O'Brien \"x\" \\ \n </>"),
        ("controls", "tab\there\r\nline\u0001end \u2028"),
        ("unicode", "Zoë ü 日本 😀"),
        ("velocity", "$!{input.x} #if(true)#end ${a}"),
        ("empty", ""),
        ("missing", null)
    ];

    /// <summary>String inputs the DTO reads as a typed value (GUID, date, number), where a hostile string is rightly rejected.</summary>
    private static readonly HashSet<string> TypedStrings = new(StringComparer.Ordinal)
    {
        "ticketId", "startedAtUtc", "endedAtUtc", "confirmedAtUtc", "verificationSessionId", "challengeId", "crmLeadId", "unitId"
    };

    private static Dictionary<string, object?> Baseline(JsonElement schemaProperties)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in schemaProperties.EnumerateObject())
        {
            result[property.Name] = property.Name switch
            {
                "ticketId" => "42",
                "conversationId" => "conv-1",
                "channel" => "Phone",
                "startedAtUtc" or "endedAtUtc" or "confirmedAtUtc" => "2026-10-08T10:00:00Z",
                "unitId" => 41230,
                "awaiting" => true,
                "companyId" => 4,
                "type" => "Paid",
                "customerKey" => "ext:Pact:3001",
                "verificationSessionId" or "challengeId" => SessionId.ToString(),
                "crmLeadId" => "12345",
                "documentType" => "Contract",
                "idempotencyKey" => "conv-1:Contract:1",
                "flowTimeoutSeconds" => "20",
                "phoneNumber" => "+971501234567",
                "code" => "123456",
                _ => "value"
            };
        }

        return result;
    }

    private static Dictionary<string, object?> Context(Dictionary<string, object?> input, params (string Name, object? Value)[] extra)
    {
        var context = new Dictionary<string, object?>
        {
            ["input"] = input,
            ["authResponse"] = new Dictionary<string, object?> { ["token_type"] = "Bearer", ["access_token"] = "tkn" }
        };
        foreach (var (name, value) in extra)
        {
            context[name] = value;
        }

        return context;
    }

    private static string Render(string template, Dictionary<string, object?> input) =>
        VelocityLite.Render(template, Context(input.ToDictionary(kv => kv.Key, kv => kv.Value)));

    private static void AssertBody(string template, Dictionary<string, object?> input, Type dtoType, string file)
    {
        var body = Render(template, input);
        using var _ = ParseOrFail(body, file, "baseline", "baseline");
        Assert.NotNull(JsonSerializer.Deserialize(body, dtoType, Web));
    }

    private static JsonDocument ParseOrFail(string text, string file, string what, string label)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new Xunit.Sdk.XunitException($"{file}: {what} ({label}) is not valid JSON: {ex.Message}{Environment.NewLine}{text}");
        }
    }

    private static HashSet<string> StringLeaves(JsonElement element)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: result.Add(e.GetString()!); break;
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value); break;
                case JsonValueKind.Array: foreach (var i in e.EnumerateArray()) Walk(i); break;
            }
        }

        Walk(element);
        return result;
    }

    /// <summary>Non-nullable constructor members without a default, as the camelCase names a client must send.</summary>
    private static IEnumerable<string> RequiredMembers(Type dto)
    {
        var ctor = dto.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var nullability = new System.Reflection.NullabilityInfoContext();
        foreach (var p in ctor.GetParameters())
        {
            if (!p.HasDefaultValue && nullability.Create(p).WriteState == System.Reflection.NullabilityState.NotNull)
            {
                yield return char.ToLowerInvariant(p.Name![0]) + p.Name[1..];
            }
        }
    }

    private static bool PropertyChainExists(JsonElement root, string chain)
    {
        var current = root;
        foreach (var part in chain.Split('.', StringSplitOptions.RemoveEmptyEntries).Skip(chain.StartsWith("$.", StringComparison.Ordinal) ? 0 : 1))
        {
            if (part == "$") continue;
            if (current.ValueKind == JsonValueKind.Array)
            {
                current = current.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(part, out _));
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next))
            {
                return false;
            }

            current = next;
        }

        return true;
    }

    private static void AssertConformsToSchema(JsonElement output, JsonElement schemaProperties, string where)
    {
        Assert.Equal(JsonValueKind.Object, output.ValueKind);
        foreach (var member in output.EnumerateObject())
        {
            Assert.True(schemaProperties.TryGetProperty(member.Name, out var declared), $"{where}: '{member.Name}' is rendered but not declared in successSchema");
            var type = declared.GetProperty("type").GetString();
            var ok = type switch
            {
                "string" => member.Value.ValueKind == JsonValueKind.String,
                "integer" => member.Value.ValueKind == JsonValueKind.Number && member.Value.TryGetInt64(out _),
                "number" => member.Value.ValueKind == JsonValueKind.Number,
                "boolean" => member.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "array" => member.Value.ValueKind == JsonValueKind.Array,
                "object" => member.Value.ValueKind == JsonValueKind.Object,
                _ => true
            };
            Assert.True(ok, $"{where}: '{member.Name}' is declared {type} but rendered {member.Value.ValueKind}: {member.Value.GetRawText()}");
        }
    }

    [GeneratedRegex(@"(?:\$esc\.url\()?\$\{input\.(\w+)\}\)?")]
    private static partial Regex InputReference();

    [GeneratedRegex(@"\$!?\{?input\.(\w+)\}?")]
    private static partial Regex InputUsage();
}
