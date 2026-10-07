using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// <b>Stub-based, not a CRM integration test.</b> These pin how
/// <see cref="CrmUnitDetailsHttpGateway"/> builds its request and reads the
/// <i>proposed</i> <c>GetUnitDetails</c> contract from a canned HTTP handler.
/// They say nothing about whether a deployed Tiger CRM implements that
/// contract — that needs the CRM route and a UAT run.
/// </summary>
public class CrmUnitDetailsHttpGatewayTests
{
    private const string SecretKey = "test-only-secret-key";

    private static CrmUnitDetailsHttpGateway Create(StubHttpMessageHandler handler, string? secret = SecretKey) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://crm.example.test/"), Timeout = TimeSpan.FromSeconds(5) },
            Options.Create(new CrmGatewayOptions { SecretKey = secret }),
            NullLogger<CrmUnitDetailsHttpGateway>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static StubHttpMessageHandler Returns(HttpStatusCode code, string json) =>
        new((_, _) => Task.FromResult(Json(code, json)));

    private const string Full = """
        { "success": true, "found": true, "unit": {
            "unitTypeName": "Apartment", "towerName": "Tower A", "bedrooms": 2, "area": 1250.5, "areaUnit": "sqft",
            "parking": [ { "number": "P-114", "level": "P1", "type": null } ],
            "expectedHandoverDate": "2027-06-30", "actualHandoverDate": "2027-07-02T00:00:00",
            "project": { "address": "Dubai", "status": "Under construction", "expectedHandoverDate": "2027-03-31",
                         "actualHandoverDate": null, "description": "A tower.", "amenities": ["Pool", " ", "Gym"] } } }
        """;

    [Fact]
    public async Task SendsCustomerAndUnit_WithTheSecretHeader()
    {
        var handler = Returns(HttpStatusCode.OK, Full);

        await Create(handler).GetUnitDetailsAsync(9001, 9200);

        Assert.Equal("https://crm.example.test/TicketingSystem/GetUnitDetails?customerId=9001&unitId=9200",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(SecretKey, handler.LastRequest.Headers.GetValues("X-SECRET-KEY").Single());
    }

    [Fact]
    public async Task MapsEveryField_KeepingUnitAndProjectHandoverSeparate()
    {
        var result = await Create(Returns(HttpStatusCode.OK, Full)).GetUnitDetailsAsync(9001, 9200);

        Assert.Equal(CrmUnitDetailsOutcome.Found, result.Outcome);
        var d = result.Details!;
        Assert.Equal(("Apartment", "Tower A", 2, 1250.5m, "sqft"), (d.UnitTypeName, d.TowerName, d.Bedrooms, d.Area, d.AreaUnit));
        Assert.Equal("P-114", Assert.Single(d.Parking!).Number);
        Assert.Equal(new DateOnly(2027, 6, 30), d.UnitExpectedHandoverDate);
        Assert.Equal(new DateOnly(2027, 7, 2), d.UnitActualHandoverDate);
        Assert.Equal(new DateOnly(2027, 3, 31), d.Project!.ExpectedHandoverDate);
        Assert.Null(d.Project.ActualHandoverDate);
        Assert.Equal(["Pool", "Gym"], d.Project.Amenities);
    }

    [Theory]
    [InlineData("\"/Date(1700000000000)/\"")]
    [InlineData("\"0001-01-01T00:00:00\"")]
    [InlineData("\"not a date\"")]
    [InlineData("\"\"")]
    public async Task UnreadableOrSentinelDates_AreNotRecorded_NeverGuessed(string date)
    {
        var json = $$"""{ "success": true, "found": true, "unit": { "expectedHandoverDate": {{date}}, "project": { "expectedHandoverDate": {{date}} } } }""";

        var d = (await Create(Returns(HttpStatusCode.OK, json)).GetUnitDetailsAsync(1, 2)).Details!;

        Assert.Null(d.UnitExpectedHandoverDate);
        Assert.Null(d.Project!.ExpectedHandoverDate);
    }

    [Fact]
    public async Task NullMembers_StayNull()
    {
        var json = """{ "success": true, "found": true, "unit": { "bedrooms": null, "parking": null, "project": null } }""";

        var d = (await Create(Returns(HttpStatusCode.OK, json)).GetUnitDetailsAsync(1, 2)).Details!;

        Assert.Null(d.Bedrooms);
        Assert.Null(d.Parking);
        Assert.Null(d.Project);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, """{ "success": true, "found": false }""")]
    public async Task UnknownRouteOrUnit_IsNotAvailable(HttpStatusCode code, string body)
    {
        var result = await Create(Returns(code, body)).GetUnitDetailsAsync(1, 2);

        Assert.Equal(CrmUnitDetailsOutcome.NotAvailable, result.Outcome);
        Assert.Null(result.Details);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    [InlineData(HttpStatusCode.BadRequest, "")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.OK, "<html>error</html>")]
    [InlineData(HttpStatusCode.OK, """{ "success": false }""")]
    public async Task CrmFailures_AreUnavailable(HttpStatusCode code, string body)
    {
        Assert.Equal(CrmUnitDetailsOutcome.Unavailable, (await Create(Returns(code, body)).GetUnitDetailsAsync(1, 2)).Outcome);
    }

    [Fact]
    public async Task NetworkFailure_IsUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("down"));

        Assert.Equal(CrmUnitDetailsOutcome.Unavailable, (await Create(handler).GetUnitDetailsAsync(1, 2)).Outcome);
    }

    [Fact]
    public async Task MissingSecret_IsUnavailable_AndCallsNothing()
    {
        var handler = Returns(HttpStatusCode.OK, Full);

        var result = await Create(handler, secret: null).GetUnitDetailsAsync(1, 2);

        Assert.Equal(CrmUnitDetailsOutcome.Unavailable, result.Outcome);
        Assert.Equal(0, handler.CallCount);
    }
}
