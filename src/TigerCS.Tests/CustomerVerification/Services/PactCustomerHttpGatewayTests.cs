using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Integrations.Modules.PactIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// <see cref="PactCustomerHttpGateway"/>'s HTTP contract with PACT's
/// <c>GET v1/contracts/{mobile}</c> / <c>GET v1/contracts/{mobile}/customer-type</c>
/// endpoints, against PACT's REAL response shape: a flat <c>data</c> array of
/// per-contract rows (never a customer object with a nested contracts array).
/// Covers the request shape (X-API-KEY header, URL-encoded mobile in the
/// path), tenantID grouping (one contract, multiple contracts for the same
/// tenant, multiple tenants), the full real field mapping,
/// <c>customerBuyerType</c> as the authoritative type with the customer-type
/// endpoint strictly a fallback, and every documented failure case (empty
/// data array, null/missing data, bad request, unauthorized/forbidden,
/// timeout, network failure, server error, malformed/empty body, missing
/// configuration). Mirrors <see cref="CrmBuyerHttpGatewayTests"/>.
/// </summary>
public class PactCustomerHttpGatewayTests
{
    private const string BaseUrl = "https://pact.example.test/";
    private const string ApiKey = "test-only-api-key";

    private static PactCustomerHttpGateway CreateGateway(StubHttpMessageHandler handler, string? apiKey = ApiKey)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(5) };
        var options = Options.Create(new PactApiOptions { BaseUrl = BaseUrl, ApiKey = apiKey });
        return new PactCustomerHttpGateway(httpClient, options, NullLogger<PactCustomerHttpGateway>.Instance);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    /// <summary>Routes the contracts endpoint and the customer-type endpoint to separate responders, recording every request URI.</summary>
    private static StubHttpMessageHandler RoutedHandler(
        Func<HttpResponseMessage> contractsResponse, Func<HttpResponseMessage> customerTypeResponse, List<string>? requestPaths = null) =>
        new((request, _) =>
        {
            requestPaths?.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/customer-type", StringComparison.Ordinal)
                ? customerTypeResponse()
                : contractsResponse());
        });

    // The real wire shape: a flat data array, one row per contract, with the
    // customer/tenant fields repeated on every row — including the financial
    // fields the gateway deliberately does not model or map.
    private const string SingleContractJson = """
        {
          "data": [
            {
              "tenantID": 7001,
              "companyID": 3,
              "projectCode": "MAR",
              "projectName": "Tiger Marina Residences",
              "unitID": 41230,
              "unitCode": "MAR-A-0304",
              "unitType": "Residential",
              "unitNumber": "0304",
              "unitStatus": "Occupied",
              "contractID": 88001,
              "contractStartDate": "2025-01-01T00:00:00",
              "contractEndDate": "2026-01-01T00:00:00",
              "contractNetAmount": 52000.50,
              "contractDiscountAmount": 0,
              "contractServicesNetAmount": 1200.00,
              "contractServicesDiscountAmount": 0,
              "customerMobile": "+971500000002",
              "customerName": "Fatima Noor",
              "customerEmail": "fatima@example.com",
              "customerBuyerType": 2,
              "grossArea": 120.5,
              "netArea": 98.2
            }
          ]
        }
        """;

    private const string SingleContractNoBuyerTypeJson = """
        {
          "data": [
            {
              "tenantID": 7001,
              "companyID": 3,
              "projectCode": "MAR",
              "projectName": "Tiger Marina Residences",
              "unitID": 41230,
              "unitCode": "MAR-A-0304",
              "unitType": "Residential",
              "unitNumber": "0304",
              "unitStatus": "Occupied",
              "contractID": 88001,
              "customerMobile": "+971500000002",
              "customerName": "Fatima Noor",
              "customerEmail": null,
              "customerBuyerType": null
            }
          ]
        }
        """;

    [Fact]
    public async Task SearchByMobileAsync_SingleContract_ReturnsSuccessWithAllRealFieldsMapped()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, SingleContractJson)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000002");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        var customer = Assert.Single(result.Customers!);
        // tenantID is the primary external PACT customer/tenant identifier.
        Assert.Equal("7001", customer.PactCustomerId);
        Assert.Equal("Fatima Noor", customer.DisplayName);
        Assert.Equal("+971500000002", customer.PhoneNumber);
        Assert.Equal("fatima@example.com", customer.Email);
        // customerBuyerType from the contracts response itself — authoritative.
        Assert.Equal("2", customer.CustomerType);
        var contract = Assert.Single(customer.Contracts);
        Assert.Equal("41230", contract.ExternalUnitId);
        Assert.Equal("88001", contract.ContractNumber);
        Assert.Equal("0304", contract.UnitNumber);
        Assert.Equal("Tiger Marina Residences", contract.ProjectName);
        Assert.Equal("Residential", contract.UnitType);
    }

    [Fact]
    public async Task SearchByMobileAsync_SendsApiKeyHeaderAndUrlEncodedNormalizedMobileInPath()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, SingleContractJson)));
        var gateway = CreateGateway(handler);

        await gateway.SearchByMobileAsync("+971 50 000 0002");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest!.Headers.GetValues("X-API-KEY")));
        Assert.StartsWith("/v1/contracts/", handler.LastRequest.RequestUri!.PathAndQuery);
        // PACT normalization strips the '+' (PACT stores numbers without it);
        // what remains is percent-encoded, never sent literally.
        Assert.DoesNotContain("+", handler.LastRequest.RequestUri.PathAndQuery);
        Assert.DoesNotContain("%2B", handler.LastRequest.RequestUri.PathAndQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Uri.EscapeDataString("971 50 000 0002"), handler.LastRequest.RequestUri.PathAndQuery);
    }

    // ---- PACT-only phone normalization: trim + remove '+', at this gateway
    // boundary and nowhere else (CRM keeps the number exactly as entered —
    // see CrmBuyerHttpGatewayTests' own URL assertion). ----

    [Fact]
    public async Task SearchByMobileAsync_PlusPrefixedMobile_PactReceivesItWithoutThePlus()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, SingleContractJson)));
        var gateway = CreateGateway(handler);

        await gateway.SearchByMobileAsync("+971501234567");

        Assert.Equal("/v1/contracts/971501234567", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SearchByMobileAsync_AlreadyNormalizedMobile_IsSentUnchanged()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, SingleContractJson)));
        var gateway = CreateGateway(handler);

        await gateway.SearchByMobileAsync("971501234567");

        Assert.Equal("/v1/contracts/971501234567", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SearchByMobileAsync_SurroundingWhitespace_IsTrimmedBeforeTheCall()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, SingleContractJson)));
        var gateway = CreateGateway(handler);

        await gateway.SearchByMobileAsync("  +971501234567  ");

        Assert.Equal("/v1/contracts/971501234567", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SearchByMobileAsync_OnlyPlusSigns_ReturnsNotFoundWithoutCallingPact()
    {
        // '+' alone normalizes to nothing searchable — and an empty path
        // segment would hit a different PACT route entirely.
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, SingleContractJson)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+");

        Assert.Equal(PactCustomerLookupOutcome.NotFound, result.Outcome);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SearchByMobileAsync_MultipleContractsForSameTenant_GroupsIntoOneCustomerWithAllUnits()
    {
        const string json = """
            {
              "data": [
                { "tenantID": 7001, "companyID": 3, "projectCode": "MAR", "projectName": "Tiger Marina Residences", "unitID": 41230, "unitCode": "MAR-A-0304", "unitType": "Residential", "unitNumber": "0304", "unitStatus": "Occupied", "contractID": 88001, "customerMobile": "+971500000002", "customerName": "Fatima Noor", "customerEmail": "fatima@example.com", "customerBuyerType": 2 },
                { "tenantID": 7001, "companyID": 3, "projectCode": "BAY", "projectName": "Tiger Bay Towers", "unitID": 52110, "unitCode": "BAY-B-1105", "unitType": "Commercial", "unitNumber": "1105", "unitStatus": "Occupied", "contractID": 88002, "customerMobile": "+971500000002", "customerName": "Fatima Noor", "customerEmail": "fatima@example.com", "customerBuyerType": 2 }
              ]
            }
            """;
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000002");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        // Two rows, one tenantID — one customer match with ALL of that
        // tenant's contracts/units, never just the first row and never an
        // auto-selected one.
        var customer = Assert.Single(result.Customers!);
        Assert.Equal("7001", customer.PactCustomerId);
        Assert.Equal(2, customer.Contracts.Count);
        Assert.Contains(customer.Contracts, c => c.ExternalUnitId == "41230" && c.ContractNumber == "88001");
        Assert.Contains(customer.Contracts, c => c.ExternalUnitId == "52110" && c.ContractNumber == "88002");
    }

    [Fact]
    public async Task SearchByMobileAsync_MultipleTenants_ReturnsOneCustomerPerTenantWithTheirOwnContracts()
    {
        const string json = """
            {
              "data": [
                { "tenantID": 7001, "companyID": 3, "projectCode": "MAR", "projectName": "Tiger Marina Residences", "unitID": 41230, "unitCode": "MAR-A-0304", "unitType": "Residential", "unitNumber": "0304", "unitStatus": "Occupied", "contractID": 88001, "customerMobile": "+971500000002", "customerName": "Fatima Noor", "customerEmail": "fatima@example.com", "customerBuyerType": 2 },
                { "tenantID": 7002, "companyID": 3, "projectCode": "BAY", "projectName": "Tiger Bay Towers", "unitID": 52110, "unitCode": "BAY-B-1105", "unitType": "Commercial", "unitNumber": "1105", "unitStatus": "Occupied", "contractID": 88002, "customerMobile": "+971500000002", "customerName": "Youssef Noor", "customerEmail": null, "customerBuyerType": 1 }
              ]
            }
            """;
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000002");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Customers!.Count);
        var fatima = Assert.Single(result.Customers, c => c.PactCustomerId == "7001");
        Assert.Equal("Fatima Noor", fatima.DisplayName);
        Assert.Equal("2", fatima.CustomerType);
        Assert.Equal("41230", Assert.Single(fatima.Contracts).ExternalUnitId);
        var youssef = Assert.Single(result.Customers, c => c.PactCustomerId == "7002");
        Assert.Equal("Youssef Noor", youssef.DisplayName);
        Assert.Equal("1", youssef.CustomerType);
        Assert.Equal("52110", Assert.Single(youssef.Contracts).ExternalUnitId);
    }

    [Fact]
    public async Task SearchByMobileAsync_BuyerTypePresent_NeverCallsCustomerTypeEndpoint()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, SingleContractJson)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000002");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        // customerBuyerType came on the contracts response — the customer-type
        // endpoint is a fallback only and must not be called.
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SearchByMobileAsync_BuyerTypeNull_FallsBackToCustomerTypeEndpoint()
    {
        var requestPaths = new List<string>();
        var handler = RoutedHandler(
            () => JsonResponse(HttpStatusCode.OK, SingleContractNoBuyerTypeJson),
            () => JsonResponse(HttpStatusCode.OK, """{ "tenantId": "7001", "customerType": "Owner" }"""),
            requestPaths);
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000002");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal("Owner", Assert.Single(result.Customers!).CustomerType);
        Assert.Equal(2, requestPaths.Count);
        Assert.EndsWith("/customer-type", requestPaths[1]);
        // The fallback call uses the same PACT-normalized number — no '+'.
        Assert.Contains("971500000002", requestPaths[1]);
        Assert.DoesNotContain("%2B", requestPaths[1], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "irrelevant")]
    [InlineData(HttpStatusCode.NotFound, "irrelevant")]
    [InlineData(HttpStatusCode.OK, "{ not valid json")]
    public async Task SearchByMobileAsync_CustomerTypeFallbackFails_LookupStillSucceedsWithNullType(
        HttpStatusCode customerTypeStatus, string customerTypeBody)
    {
        var handler = RoutedHandler(
            () => JsonResponse(HttpStatusCode.OK, SingleContractNoBuyerTypeJson),
            () => JsonResponse(customerTypeStatus, customerTypeBody));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000002");

        // The fallback call is best-effort: its failure never degrades the
        // already-successful contracts lookup.
        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        Assert.Null(Assert.Single(result.Customers!).CustomerType);
    }

    [Fact]
    public async Task SearchByMobileAsync_UnitIdMissing_FallsBackToUnitCodeThenUnitNumber()
    {
        // unitID is the primary ExternalUnitId; unitCode and unitNumber are
        // fallbacks, in that order, for rows PACT sent without a unitID.
        const string json = """
            {
              "data": [
                { "tenantID": 7001, "companyID": 3, "projectCode": "MAR", "projectName": "P1", "unitID": null, "unitCode": "104-2304", "unitType": null, "unitNumber": "2304", "unitStatus": null, "contractID": 88001, "customerMobile": "1", "customerName": "Fatima Noor", "customerEmail": null, "customerBuyerType": 2 },
                { "tenantID": 7001, "companyID": 3, "projectCode": "MAR", "projectName": "P2", "unitID": null, "unitCode": null, "unitType": null, "unitNumber": "1105", "unitStatus": null, "contractID": 88002, "customerMobile": "1", "customerName": "Fatima Noor", "customerEmail": null, "customerBuyerType": 2 }
              ]
            }
            """;
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("1");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        var customer = Assert.Single(result.Customers!);
        Assert.Equal(2, customer.Contracts.Count);
        Assert.Contains(customer.Contracts, c => c.ExternalUnitId == "104-2304");
        Assert.Contains(customer.Contracts, c => c.ExternalUnitId == "1105");
    }

    [Fact]
    public async Task SearchByMobileAsync_RowWithNoUnitIdentifiers_IsDroppedNotFabricated()
    {
        // First row carries no unitID/unitCode/unitNumber at all — its
        // contractID alone never stands in as a unit identifier (a contract
        // id identifies the contract, not the unit), so the row is dropped.
        // Second row keeps its unitID as normal.
        const string json = """
            {
              "data": [
                { "tenantID": 7001, "companyID": 3, "projectCode": null, "projectName": "P1", "unitID": null, "unitCode": null, "unitType": null, "unitNumber": null, "unitStatus": null, "contractID": 99, "customerMobile": "1", "customerName": "Fatima Noor", "customerEmail": null, "customerBuyerType": 2 },
                { "tenantID": 7001, "companyID": 3, "projectCode": null, "projectName": "P2", "unitID": 41230, "unitCode": null, "unitType": null, "unitNumber": null, "unitStatus": null, "contractID": 88001, "customerMobile": "1", "customerName": "Fatima Noor", "customerEmail": null, "customerBuyerType": 2 }
              ]
            }
            """;
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("1");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        var contract = Assert.Single(Assert.Single(result.Customers!).Contracts);
        Assert.Equal("41230", contract.ExternalUnitId);
    }

    [Fact]
    public async Task SearchByMobileAsync_EmptyDataArray_ReturnsNotFound()
    {
        // An empty data array is PACT answering "nothing on file", not an error.
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, """{ "data": [] }""")));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.NotFound, result.Outcome);
        Assert.Null(result.Customers);
        Assert.Equal(1, handler.CallCount); // no pointless customer-type call for a non-match
    }

    [Theory]
    [InlineData("""{ "data": null }""")]
    [InlineData("{ }")]
    public async Task SearchByMobileAsync_NullOrMissingDataArray_ReturnsInvalidResponse(string json)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task SearchByMobileAsync_HttpNotFound_ReturnsNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.NotFound, result.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task SearchByMobileAsync_UnauthorizedOrForbidden_ReturnsUnauthorized(HttpStatusCode statusCode)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task SearchByMobileAsync_BadRequest_ReturnsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("not-a-real-mobile");

        Assert.Equal(PactCustomerLookupOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task SearchByMobileAsync_MalformedJsonBody_ReturnsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{ this is not valid json")));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task SearchByMobileAsync_EmptyBody_ReturnsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, string.Empty)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task SearchByMobileAsync_NetworkFailure_ReturnsUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Simulated DNS/connection failure."));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task SearchByMobileAsync_Timeout_ReturnsUnavailable()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMilliseconds(100) };
        var gateway = new PactCustomerHttpGateway(
            httpClient, Options.Create(new PactApiOptions { BaseUrl = BaseUrl, ApiKey = ApiKey }), NullLogger<PactCustomerHttpGateway>.Instance);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task SearchByMobileAsync_ServerError_ReturnsUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task SearchByMobileAsync_MissingApiKey_ReturnsUnavailableWithoutCallingPact()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, SingleContractJson)));
        var gateway = CreateGateway(handler, apiKey: null);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.Unavailable, result.Outcome);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- Base-address resolution: a slashless BaseUrl with a path prefix
    // must not silently lose the prefix (relative-URI resolution drops the
    // last path segment of a slashless base, turning every lookup into a
    // 404 -> "customer not found"). ----

    [Theory]
    [InlineData("https://pact.example.test/", "https://pact.example.test/")]
    [InlineData("https://pact.example.test", "https://pact.example.test/")]
    [InlineData("https://pact.example.test/api", "https://pact.example.test/api/")]
    [InlineData("https://pact.example.test/api/", "https://pact.example.test/api/")]
    public void ResolveBaseAddress_AlwaysEndsWithASlash(string configured, string expected)
    {
        var options = new PactApiOptions { BaseUrl = configured };

        Assert.Equal(new Uri(expected), options.ResolveBaseAddress());
    }

    [Fact]
    public void ResolveBaseAddress_PathPrefix_SurvivesRelativeUriResolution()
    {
        // The exact failure mode being prevented: without the trailing
        // slash, "…/api" + "v1/contracts/…" resolves to "…/v1/contracts/…",
        // silently dropping "/api".
        var baseAddress = new PactApiOptions { BaseUrl = "https://pact.example.test/api" }.ResolveBaseAddress();

        var resolved = new Uri(baseAddress!, "v1/contracts/971501234567");

        Assert.Equal("/api/v1/contracts/971501234567", resolved.AbsolutePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveBaseAddress_MissingBaseUrl_ReturnsNull(string? configured)
    {
        Assert.Null(new PactApiOptions { BaseUrl = configured }.ResolveBaseAddress());
    }

    [Fact]
    public async Task SearchByMobileAsync_MissingBaseUrl_ReturnsUnavailable()
    {
        // Typed HttpClient with no BaseAddress rejects the relative URI with
        // an InvalidOperationException — collapsed to Unavailable, never an
        // unhandled exception or a startup failure.
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, SingleContractJson)));
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var gateway = new PactCustomerHttpGateway(
            httpClient, Options.Create(new PactApiOptions { BaseUrl = null, ApiKey = ApiKey }), NullLogger<PactCustomerHttpGateway>.Instance);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.Unavailable, result.Outcome);
    }
}
