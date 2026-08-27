using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// CRM Buyer Lookup: <see cref="CrmBuyerHttpGateway"/>'s HTTP contract with
/// the real <c>GET /TicketingSystem/GetBuyerByPhone</c> endpoint — request
/// shape (header, URL-encoded phone number) and every documented response
/// case (found, multiple units/buyers, not found, unauthorized, timeout,
/// network failure, malformed/empty body).
/// </summary>
public class CrmBuyerHttpGatewayTests
{
    private const string BaseUrl = "https://crm.example.test/";
    private const string SecretKey = "test-only-secret-key";

    private static CrmBuyerHttpGateway CreateGateway(StubHttpMessageHandler handler, string? secretKey = SecretKey)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(5) };
        var options = Options.Create(new CrmGatewayOptions { BaseUrl = BaseUrl, SecretKey = secretKey });
        return new CrmBuyerHttpGateway(httpClient, options, NullLogger<CrmBuyerHttpGateway>.Instance);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private const string SingleBuyerSingleUnitJson = """
        {
          "success": true,
          "found": true,
          "message": "Buyer found successfully.",
          "buyers": [
            {
              "customer": {
                "customerId": 123,
                "fullNameEnglish": "John Buyer",
                "fullNameArabic": "جون",
                "mobileNumber": "+971500000123",
                "email": "john@example.com"
              },
              "units": [
                {
                  "leadId": 100,
                  "leadStatus": 8,
                  "leadStatusName": "Sold",
                  "unitId": 500,
                  "unitNumber": "1204",
                  "unitStatus": 3,
                  "unitType": 2,
                  "floorNumber": 12,
                  "projectId": 79,
                  "projectName": "Tiger Tower",
                  "projectArabicName": "برج تايجر",
                  "customerType": 1,
                  "customerTypeName": "Buyer"
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task GetBuyerByPhoneAsync_BuyerFound_ReturnsSuccessWithMappedFields()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, SingleBuyerSingleUnitJson)));
        var gateway = CreateGateway(handler);

        var result = await gateway.GetBuyerByPhoneAsync("+971500000123");

        Assert.Equal(CrmBuyerLookupOutcome.Success, result.Outcome);
        var buyer = Assert.Single(result.Buyers!);
        Assert.Equal(123, buyer.Customer.CustomerId);
        Assert.Equal("John Buyer", buyer.Customer.FullNameEnglish);
        var unit = Assert.Single(buyer.Units);
        Assert.Equal(500, unit.UnitId);
        Assert.Equal(8, unit.LeadStatus);
        Assert.Equal("Sold", unit.LeadStatusName);
        Assert.Equal(1, unit.CustomerType);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_SendsSecretHeaderAndUrlEncodedPhoneNumber()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, SingleBuyerSingleUnitJson)));
        var gateway = CreateGateway(handler);

        await gateway.GetBuyerByPhoneAsync("+971 50 000 0123");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(SecretKey, Assert.Single(handler.LastRequest!.Headers.GetValues("X-SECRET-KEY")));
        Assert.StartsWith("/TicketingSystem/GetBuyerByPhone?phoneNumber=", handler.LastRequest.RequestUri!.PathAndQuery);
        // '+' and spaces must be percent-encoded, never sent literally (a literal '+' in a query string means space).
        Assert.DoesNotContain("+971 50", handler.LastRequest.RequestUri.Query);
        Assert.Contains(Uri.EscapeDataString("+971 50 000 0123"), handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_MultipleUnitsForOneBuyer_ReturnsAllUnits()
    {
        const string json = """
            {
              "success": true,
              "found": true,
              "message": "Buyer found successfully.",
              "buyers": [
                {
                  "customer": { "customerId": 1, "fullNameEnglish": "A", "fullNameArabic": null, "mobileNumber": "1", "email": null },
                  "units": [
                    { "leadId": 10, "leadStatus": 8, "leadStatusName": "Sold", "unitId": 100, "unitNumber": "101", "unitStatus": 1, "unitType": 1, "floorNumber": 1, "projectId": 1, "projectName": "P1", "projectArabicName": null, "customerType": 1, "customerTypeName": "Buyer" },
                    { "leadId": 11, "leadStatus": 9, "leadStatusName": "Contract", "unitId": 101, "unitNumber": "102", "unitStatus": 1, "unitType": 1, "floorNumber": 2, "projectId": 1, "projectName": "P1", "projectArabicName": null, "customerType": 1, "customerTypeName": "Buyer" }
                  ]
                }
              ]
            }
            """;
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.GetBuyerByPhoneAsync("1");

        Assert.Equal(CrmBuyerLookupOutcome.Success, result.Outcome);
        var buyer = Assert.Single(result.Buyers!);
        Assert.Equal(2, buyer.Units.Count);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_MultipleBuyers_ReturnsAllBuyersUnfiltered()
    {
        const string json = """
            {
              "success": true,
              "found": true,
              "message": null,
              "buyers": [
                { "customer": { "customerId": 1, "fullNameEnglish": "A", "fullNameArabic": null, "mobileNumber": "1", "email": null },
                  "units": [ { "leadId": 10, "leadStatus": 8, "leadStatusName": "Sold", "unitId": 100, "unitNumber": "101", "unitStatus": 1, "unitType": 1, "floorNumber": 1, "projectId": 1, "projectName": "P1", "projectArabicName": null, "customerType": 1, "customerTypeName": "Buyer" } ] },
                { "customer": { "customerId": 2, "fullNameEnglish": "B", "fullNameArabic": null, "mobileNumber": "1", "email": null },
                  "units": [ { "leadId": 20, "leadStatus": 9, "leadStatusName": "Contract", "unitId": 200, "unitNumber": "201", "unitStatus": 1, "unitType": 1, "floorNumber": 3, "projectId": 2, "projectName": "P2", "projectArabicName": null, "customerType": 1, "customerTypeName": "Buyer" } ] }
              ]
            }
            """;
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.GetBuyerByPhoneAsync("1");

        Assert.Equal(CrmBuyerLookupOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Buyers!.Count);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_FoundFalse_ReturnsNotFound()
    {
        const string json = """{ "success": true, "found": false, "message": "No buyer found.", "buyers": [] }""";
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.NotFound, result.Outcome);
        Assert.Null(result.Buyers);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_HttpNotFound_ReturnsNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var gateway = CreateGateway(handler);

        var result = await gateway.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_Unauthorized_ReturnsUnauthorized()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var gateway = CreateGateway(handler);

        var result = await gateway.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_BadRequest_ReturnsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var gateway = CreateGateway(handler);

        var result = await gateway.GetBuyerByPhoneAsync("not-a-real-phone");

        Assert.Equal(CrmBuyerLookupOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_MalformedJsonBody_ReturnsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{ this is not valid json")));
        var gateway = CreateGateway(handler);

        var result = await gateway.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_EmptyBody_ReturnsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, string.Empty)));
        var gateway = CreateGateway(handler);

        var result = await gateway.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_SuccessFalse_ReturnsInvalidResponse()
    {
        const string json = """{ "success": false, "found": false, "message": "Invalid phone number format.", "buyers": null }""";
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var gateway = CreateGateway(handler);

        var result = await gateway.GetBuyerByPhoneAsync("bad-phone");

        Assert.Equal(CrmBuyerLookupOutcome.InvalidResponse, result.Outcome);
        Assert.Equal("Invalid phone number format.", result.Message);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_NetworkFailure_ReturnsUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Simulated DNS/connection failure."));
        var gateway = CreateGateway(handler);

        var result = await gateway.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_Timeout_ReturnsUnavailable()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMilliseconds(100) };
        var gateway = new CrmBuyerHttpGateway(
            httpClient, Options.Create(new CrmGatewayOptions { BaseUrl = BaseUrl, SecretKey = SecretKey }), NullLogger<CrmBuyerHttpGateway>.Instance);

        var result = await gateway.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_UnexpectedStatusCode_ReturnsUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var gateway = CreateGateway(handler);

        var result = await gateway.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_MissingSecretKey_ReturnsUnavailableWithoutCallingCrm()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, SingleBuyerSingleUnitJson)));
        var gateway = CreateGateway(handler, secretKey: null);

        var result = await gateway.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
        Assert.Equal(0, handler.CallCount);
    }
}
