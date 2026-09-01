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
/// endpoints — request shape (X-API-KEY header, URL-encoded mobile in the
/// path), the conditional customer-type call, and every documented response
/// case (found, multiple contracts, not found, bad request, unauthorized/
/// forbidden, timeout, network failure, server error, malformed/empty body,
/// missing configuration). Mirrors <see cref="CrmBuyerHttpGatewayTests"/>.
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

    private const string TenantWithTypeAndContractsJson = """
        {
          "tenantId": "PACT-TNT-7001",
          "tenantName": "Fatima Noor",
          "mobile": "+971500000002",
          "email": "fatima@example.com",
          "customerType": "Tenant",
          "contracts": [
            {
              "tenantId": "PACT-TNT-7001",
              "contractNumber": "PACT-CNT-88001",
              "unitCode": "MAR-A-0304",
              "unitNumber": "0304",
              "projectName": "Tiger Marina Residences",
              "unitType": "Residential"
            }
          ]
        }
        """;

    private const string TenantWithoutTypeJson = """
        {
          "tenantId": "PACT-TNT-7001",
          "tenantName": "Fatima Noor",
          "mobile": "+971500000002",
          "email": null,
          "customerType": null,
          "contracts": [
            {
              "tenantId": "PACT-TNT-7001",
              "contractNumber": "PACT-CNT-88001",
              "unitCode": "MAR-A-0304",
              "unitNumber": "0304",
              "projectName": "Tiger Marina Residences",
              "unitType": "Residential"
            }
          ]
        }
        """;

    [Fact]
    public async Task SearchByMobileAsync_CustomerFound_ReturnsSuccessWithMappedFields()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, TenantWithTypeAndContractsJson)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000002");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        var customer = Assert.Single(result.Customers!);
        Assert.Equal("PACT-TNT-7001", customer.PactCustomerId);
        Assert.Equal("Fatima Noor", customer.DisplayName);
        Assert.Equal("+971500000002", customer.PhoneNumber);
        Assert.Equal("fatima@example.com", customer.Email);
        Assert.Equal("Tenant", customer.CustomerType);
        var contract = Assert.Single(customer.Contracts);
        Assert.Equal("MAR-A-0304", contract.ExternalUnitId);
        Assert.Equal("PACT-CNT-88001", contract.ContractNumber);
        Assert.Equal("0304", contract.UnitNumber);
        Assert.Equal("Tiger Marina Residences", contract.ProjectName);
        Assert.Equal("Residential", contract.UnitType);
    }

    [Fact]
    public async Task SearchByMobileAsync_SendsApiKeyHeaderAndUrlEncodedMobileInPath()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, TenantWithTypeAndContractsJson)));
        var gateway = CreateGateway(handler);

        await gateway.SearchByMobileAsync("+971 50 000 0002");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest!.Headers.GetValues("X-API-KEY")));
        Assert.StartsWith("/v1/contracts/", handler.LastRequest.RequestUri!.PathAndQuery);
        // '+' and spaces must be percent-encoded, never sent literally.
        Assert.DoesNotContain("+971 50", handler.LastRequest.RequestUri.PathAndQuery);
        Assert.Contains(Uri.EscapeDataString("+971 50 000 0002"), handler.LastRequest.RequestUri.PathAndQuery);
    }

    [Fact]
    public async Task SearchByMobileAsync_ContractsResponseCarriesCustomerType_NeverCallsCustomerTypeEndpoint()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, TenantWithTypeAndContractsJson)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000002");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        // "When required" only — the type was already on the contracts response.
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SearchByMobileAsync_CustomerTypeMissing_FetchesItFromCustomerTypeEndpoint()
    {
        var requestPaths = new List<string>();
        var handler = RoutedHandler(
            () => JsonResponse(HttpStatusCode.OK, TenantWithoutTypeJson),
            () => JsonResponse(HttpStatusCode.OK, """{ "tenantId": "PACT-TNT-7001", "customerType": "Owner" }"""),
            requestPaths);
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000002");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal("Owner", Assert.Single(result.Customers!).CustomerType);
        Assert.Equal(2, requestPaths.Count);
        Assert.EndsWith("/customer-type", requestPaths[1]);
        Assert.Contains(Uri.EscapeDataString("+971500000002"), requestPaths[1]);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "irrelevant")]
    [InlineData(HttpStatusCode.NotFound, "irrelevant")]
    [InlineData(HttpStatusCode.OK, "{ not valid json")]
    public async Task SearchByMobileAsync_CustomerTypeCallFails_LookupStillSucceedsWithNullType(
        HttpStatusCode customerTypeStatus, string customerTypeBody)
    {
        var handler = RoutedHandler(
            () => JsonResponse(HttpStatusCode.OK, TenantWithoutTypeJson),
            () => JsonResponse(customerTypeStatus, customerTypeBody));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000002");

        // The secondary call is best-effort: its failure never degrades the
        // already-successful contracts lookup.
        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        Assert.Null(Assert.Single(result.Customers!).CustomerType);
    }

    [Fact]
    public async Task SearchByMobileAsync_MultipleContracts_ReturnsAllOfThem_NeverJustTheFirst()
    {
        const string json = """
            {
              "tenantId": "PACT-TNT-7001",
              "tenantName": "Fatima Noor",
              "mobile": "1",
              "customerType": "Tenant",
              "contracts": [
                { "tenantId": "PACT-TNT-7001", "contractNumber": "C-1", "unitCode": "U-100", "unitNumber": "101", "projectName": "P1", "unitType": "Residential" },
                { "tenantId": "PACT-TNT-7001", "contractNumber": "C-2", "unitCode": "U-200", "unitNumber": "201", "projectName": "P2", "unitType": "Commercial" }
              ]
            }
            """;
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("1");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        var customer = Assert.Single(result.Customers!);
        Assert.Equal(2, customer.Contracts.Count);
    }

    [Fact]
    public async Task SearchByMobileAsync_ContractRowWithNoIdentifiers_IsDroppedNotFabricated()
    {
        const string json = """
            {
              "tenantId": "PACT-TNT-7001",
              "tenantName": "Fatima Noor",
              "customerType": "Tenant",
              "contracts": [
                { "tenantId": "PACT-TNT-7001", "contractNumber": null, "unitCode": null, "unitNumber": null, "projectName": "P1", "unitType": null },
                { "tenantId": "PACT-TNT-7001", "contractNumber": "C-2", "unitCode": null, "unitNumber": null, "projectName": "P2", "unitType": null }
              ]
            }
            """;
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("1");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        var contract = Assert.Single(Assert.Single(result.Customers!).Contracts);
        // The second row falls back to its contract number as the external id.
        Assert.Equal("C-2", contract.ExternalUnitId);
    }

    [Fact]
    public async Task SearchByMobileAsync_TenantWithNoContracts_StillSuccessWithEmptyContractsList()
    {
        const string json = """{ "tenantId": "PACT-TNT-7001", "tenantName": "Fatima Noor", "customerType": "Tenant", "contracts": [] }""";
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000002");

        Assert.Equal(PactCustomerLookupOutcome.Success, result.Outcome);
        Assert.Empty(Assert.Single(result.Customers!).Contracts);
    }

    [Fact]
    public async Task SearchByMobileAsync_EmptyMatchBody_ReturnsNotFound()
    {
        // A 200 whose body carries no tenant identity and no contracts is
        // PACT answering "nothing on file", not an error.
        const string json = """{ "tenantId": null, "tenantName": null, "mobile": null, "contracts": [] }""";
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.NotFound, result.Outcome);
        Assert.Null(result.Customers);
        Assert.Equal(1, handler.CallCount); // no pointless customer-type call for a non-match
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
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, TenantWithTypeAndContractsJson)));
        var gateway = CreateGateway(handler, apiKey: null);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.Unavailable, result.Outcome);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SearchByMobileAsync_MissingBaseUrl_ReturnsUnavailable()
    {
        // Typed HttpClient with no BaseAddress rejects the relative URI with
        // an InvalidOperationException — collapsed to Unavailable, never an
        // unhandled exception or a startup failure.
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, TenantWithTypeAndContractsJson)));
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var gateway = new PactCustomerHttpGateway(
            httpClient, Options.Create(new PactApiOptions { BaseUrl = null, ApiKey = ApiKey }), NullLogger<PactCustomerHttpGateway>.Instance);

        var result = await gateway.SearchByMobileAsync("+971500000000");

        Assert.Equal(PactCustomerLookupOutcome.Unavailable, result.Outcome);
    }
}
