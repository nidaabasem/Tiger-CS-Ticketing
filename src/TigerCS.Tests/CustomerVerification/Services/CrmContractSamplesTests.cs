using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// The sample JSON in <c>docs/Genesys/crm-contracts/</c> is the contract the CRM
/// team implements against (<c>docs/Genesys/CRM-Required-Contracts.md</c>).
/// These tests parse every sample into the exact C# types TigerCS compiles
/// against, so the document cannot drift from the code: a sample that stops
/// deserialising, or breaks a rule the verification/document flow relies on,
/// fails the build.
/// </summary>
public class CrmContractSamplesTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static string Sample(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "Genesys", "crm-contracts")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "docs", "Genesys", "crm-contracts", name));
    }

    // The envelope shapes the contract defines (success/found/message, like GetBuyerByPhone).
    private sealed record UnitEnvelope(bool Success, bool Found, string? Message, CrmUnitResult? Unit);
    private sealed record UnitsEnvelope(bool Success, bool Found, string? Message, List<CrmUnitResult> Units);
    private sealed record ContactsEnvelope(bool Success, bool Found, string? Message, List<CrmContactResult> Contacts);

    [Fact]
    public void GetUnit_Sample_ParsesIntoCrmUnitResult()
    {
        var envelope = JsonSerializer.Deserialize<UnitEnvelope>(Sample("get-unit.200.json"), Json)!;

        Assert.True(envelope.Success && envelope.Found);
        Assert.Equal(new CrmUnitResult("1101", "1205", "Tiger Sky Tower", "Tower 1", "Residential"), envelope.Unit);
        Assert.InRange(envelope.Unit!.CrmUnitId.Length, 1, 64);   // UnitReferences.CrmUnitId nvarchar(64)
        Assert.InRange(envelope.Unit.UnitNumber.Length, 1, 50);   // UnitReferences.UnitNumber nvarchar(50), required
    }

    [Fact]
    public void UnitNotFound_IsA200WithFoundFalse_NotAnError()
    {
        var envelope = JsonSerializer.Deserialize<UnitEnvelope>(Sample("get-unit.not-found.200.json"), Json)!;

        Assert.True(envelope.Success);
        Assert.False(envelope.Found);
        Assert.Null(envelope.Unit);
    }

    [Fact]
    public void SearchUnits_Samples_Parse_AndAmbiguousNumbersCarryDistinctUnitIds()
    {
        var many = JsonSerializer.Deserialize<UnitsEnvelope>(Sample("search-units.200.json"), Json)!;
        Assert.Equal(2, many.Units.Count);
        Assert.Equal(many.Units.Count, many.Units.Select(u => u.CrmUnitId).Distinct().Count());

        var none = JsonSerializer.Deserialize<UnitsEnvelope>(Sample("search-units.none.200.json"), Json)!;
        Assert.True(none.Success);
        Assert.Empty(none.Units);
    }

    [Fact]
    public void GetUnitContacts_Sample_ParsesIntoCrmContactResult_AndObeysTheRulesTheFlowsRelyOn()
    {
        var envelope = JsonSerializer.Deserialize<ContactsEnvelope>(Sample("get-unit-contacts.200.json"), Json)!;

        var owner = envelope.Contacts.Single(c => c.ContactType == ContactType.Owner);
        var representative = envelope.Contacts.Single(c => c.ContactType == ContactType.Representative);

        // Contact ids are unique (ContactReferences.CrmContactId is globally unique and a row belongs to ONE unit).
        Assert.Equal(envelope.Contacts.Count, envelope.Contacts.Select(c => c.CrmContactId).Distinct().Count());
        Assert.All(envelope.Contacts, c => Assert.InRange(c.CrmContactId.Length, 1, 64));

        // A representative names the contact (on this unit) it represents; owners/tenants name none.
        Assert.Equal(owner.CrmContactId, representative.AuthorizedRepresentativeOfCrmContactId);
        Assert.Null(owner.AuthorizedRepresentativeOfCrmContactId);

        // The document flow resolves the CRM customer from the Owner contact's phone.
        Assert.True(CustomerPhoneNumber.LooksLikeNumber(owner.ContactChannel));
        Assert.True(CustomerPhoneNumber.Normalize(owner.ContactChannel).Length >= 7);
    }

    [Fact]
    public void ContactNotFoundUnit_Sample_ParsesAsFoundFalse()
    {
        var envelope = JsonSerializer.Deserialize<ContactsEnvelope>(Sample("get-unit-contacts.unit-not-found.200.json"), Json)!;

        Assert.False(envelope.Found);
        Assert.Empty(envelope.Contacts);
    }

    [Fact]
    public void ErrorSample_IsTheSuccessFalseEnvelope()
    {
        using var doc = JsonDocument.Parse(Sample("error.problem.json"));
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    // ---- GetBuyerByPhone: what it already provides, proven on its real parser ----

    private static async Task<IReadOnlyList<TigerCS.Application.Modules.CustomerVerification.Dto.CrmBuyerMatchDto>> ParseBuyersAsync()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Sample("get-buyer-by-phone.200.json"), Encoding.UTF8, "application/json")
        }));
        var gateway = new CrmBuyerHttpGateway(
            new HttpClient(handler) { BaseAddress = new Uri("https://crm.example.test/") },
            Options.Create(new CrmGatewayOptions { BaseUrl = "https://crm.example.test/", SecretKey = "k" }),
            NullLogger<CrmBuyerHttpGateway>.Instance);

        var result = await gateway.GetBuyerByPhoneAsync("+971501234567");
        Assert.Equal(CrmBuyerLookupOutcome.Success, result.Outcome);
        return result.Buyers!;
    }

    [Fact]
    public async Task GetBuyerByPhone_AlreadySupplies_TheUnitAndContactFieldsAMappingNeeds()
    {
        var buyer = Assert.Single(await ParseBuyersAsync());
        var unit = buyer.Units[0];

        // Unit: unitId → crmUnitId, unitNumber, projectName → propertyName. No tower, no unit-type label.
        Assert.Equal("1101", unit.UnitId.ToString());
        Assert.Equal("1205", unit.UnitNumber);
        Assert.Equal("Tiger Sky Tower", unit.ProjectName);
        Assert.Equal(2, unit.UnitType); // a numeric code: there is no label to put in UnitType

        // Contact: customer fields cover name and channel; no tenant / representative information exists.
        Assert.Equal(9001, buyer.Customer.CustomerId);
        Assert.Equal("Ahmed Ali", buyer.Customer.FullNameEnglish);
        Assert.Equal("+971501234567", buyer.Customer.MobileNumber);
        Assert.All(buyer.Units, u => Assert.Equal(1, u.CustomerType)); // buyers only

        // A customer's two units have two LeadIDs — what GetCustomerDocuments is keyed by.
        Assert.Equal([12345, 12346], buyer.Units.Select(u => u.LeadId).ToArray());
    }

    [Fact]
    public async Task TheUnitSample_AndTheBuyerUnit_AreRecognisedAsTheSameUnit_ByTheDocumentFlow()
    {
        // crmUnitId == String(unitId) is what lets the document flow match the verified unit to the buyer's lead exactly.
        var unitSample = JsonSerializer.Deserialize<UnitEnvelope>(Sample("get-unit.200.json"), Json)!.Unit!;
        var buyerUnit = (await ParseBuyersAsync())[0].Units[0];

        Assert.Equal(unitSample.CrmUnitId, buyerUnit.UnitId.ToString());
        Assert.Equal(unitSample.UnitNumber, buyerUnit.UnitNumber);
        Assert.Equal(unitSample.PropertyName, buyerUnit.ProjectName);
    }

    [Fact]
    public async Task GetCustomerDocuments_Sample_ParsesThroughTheRealGateway()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Sample("get-customer-documents.200.json"), Encoding.UTF8, "application/json")
        }));
        var gateway = new CrmDocumentHttpGateway(
            new HttpClient(handler) { BaseAddress = new Uri("https://crm.example.test/") },
            Options.Create(new CrmGatewayOptions { BaseUrl = "https://crm.example.test/", SecretKey = "k" }),
            NullLogger<CrmDocumentHttpGateway>.Instance);

        var listing = await gateway.ListAsync(CrmDocumentType.Contract, 9001, 12345);

        Assert.True(listing.SelectionRequired);
        Assert.Equal(["5001", "5002"], listing.Records.Select(r => r.RecordId).ToArray());
        Assert.Equal("/Uploads/Contracts/5001.pdf", listing.Records[0].FileReference);
    }
}
