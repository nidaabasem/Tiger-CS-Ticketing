using System.Linq;
using System.Text.Json;
using System.Web.Mvc;
using CRM_Performance.Controllers;
using CRM_Performance.Models;
using Xunit;

namespace Harness
{
    [Collection("serial")]
    public class GetUnitDetailsTests
    {
        const string Key = "unit-test-key";
        const int Buyer = (int)CustomerType.Buyer;

        // Customer 9001 owns unit 9200 through Sold lead 9100 and unit 9201 through Contract lead 9101.
        // Customer 7777 owns unit 5555 through Sold lead 5000.
        // Unit 9200 also has an older Cancelled booking (lead 8000) by customer 7777, and a newer Hot lead 9999 for 9001.
        static TicketingSystemController Arrange(string header = Key, string configured = Key)
        {
            var db = new FakeDb();
            db.P.Add(new tblProjects { ID = 79, Name = "Tiger Tower" });
            db.U.AddRange(new[] {
                new tblUnits { ID = 9200, Number = "1204", ProjectID = 79 },
                new tblUnits { ID = 9201, Number = "0507", ProjectID = 79 },
                new tblUnits { ID = 5555, Number = "0101", ProjectID = 79 } });
            db.L.AddRange(new[] {
                new tblLeads { ID = 9100, Status = (int)LeadStatus.Sold },
                new tblLeads { ID = 9101, Status = (int)LeadStatus.Contract },
                new tblLeads { ID = 5000, Status = (int)LeadStatus.Sold },
                new tblLeads { ID = 8000, Status = (int)LeadStatus.Cancelled },
                new tblLeads { ID = 9999, Status = (int)LeadStatus.Hot } });
            db.LC.AddRange(new[] {
                new tblLeadCustomers { LeadID = 9100, CustomerID = 9001, CustomerType = Buyer, UnitID = 9200 },
                new tblLeadCustomers { LeadID = 9101, CustomerID = 9001, CustomerType = Buyer, UnitID = 9201 },
                new tblLeadCustomers { LeadID = 5000, CustomerID = 7777, CustomerType = Buyer, UnitID = 5555 },
                new tblLeadCustomers { LeadID = 8000, CustomerID = 7777, CustomerType = Buyer, UnitID = 9200 },
                new tblLeadCustomers { LeadID = 9999, CustomerID = 9001, CustomerType = Buyer, UnitID = 9200 } });
            DbHolder.Current = db; DbHolder.Throw = false; CRMHelpers.Logged.Clear();
            System.Configuration.ConfigurationManager.AppSettings["TicketingSecretKey"] = configured;
            System.Configuration.ConfigurationManager.AppSettings["TicketingReleaseSale"] = null;
            var c = new TicketingSystemController();
            if (header != null) c.Request.Headers["X-SECRET-KEY"] = header;
            return c;
        }

        static (int Status, JsonElement Body, JsonResult Raw) Run(TicketingSystemController c, long customer, long unit, long lead, bool sale = false)
        {
            var r = (JsonResult)c.GetUnitDetails(customer, unit, lead, sale);
            return (c.Response.StatusCode, JsonSerializer.SerializeToElement(r.Data), r);
        }

        // ---- authentication ----
        [Theory] [InlineData(null)] [InlineData("")] [InlineData("wrong")]
        public void BadOrMissingKey_Is401_Json_NoRedirect_NoData(string header)
        {
            var c = Arrange(header);
            var (s, b, raw) = Run(c, 9001, 9200, 9100);
            Assert.Equal(401, s);
            Assert.False(b.GetProperty("success").GetBoolean());
            Assert.False(b.TryGetProperty("unit", out _));
            Assert.True(c.Response.SuppressFormsAuthenticationRedirect);
            Assert.True(c.Response.TrySkipIisCustomErrors);
            Assert.Equal(JsonRequestBehavior.AllowGet, raw.Behavior);
        }

        [Fact] public void KeyNotConfigured_Is503_NotOpen()
        {
            var (s, b, _) = Run(Arrange(configured: ""), 9001, 9200, 9100);
            Assert.Equal(503, s); Assert.False(b.TryGetProperty("unit", out _));
        }

        // ---- input ----
        [Theory] [InlineData(0, 9200, 9100)] [InlineData(9001, 0, 9100)] [InlineData(9001, 9200, 0)]
        public void MissingIds_Are400(long cu, long un, long le)
        {
            var (s, b, _) = Run(Arrange(), cu, un, le);
            Assert.Equal(400, s); Assert.False(b.GetProperty("success").GetBoolean());
        }

        // ---- ownership / lead binding ----
        [Fact] public void OwnUnit_OwnLead_Found_WithContractEnvelope()
        {
            var (s, b, _) = Run(Arrange(), 9001, 9200, 9100);
            Assert.Equal(200, s);
            Assert.True(b.GetProperty("success").GetBoolean());
            Assert.True(b.GetProperty("found").GetBoolean());
            var unit = b.GetProperty("unit");
            foreach (var m in new[] { "unitTypeName", "towerName", "bedrooms", "area", "areaUnit", "parking", "expectedHandoverDate", "actualHandoverDate", "project" })
                Assert.True(unit.TryGetProperty(m, out _), m);
            foreach (var m in new[] { "address", "status", "expectedHandoverDate", "actualHandoverDate", "description", "amenities", "completionPercentage", "expectedCompletionDate", "actualCompletionDate" })
                Assert.True(unit.GetProperty("project").TryGetProperty(m, out _), m);
        }

        [Fact] public void ContractLead_IsEligible()
            => Assert.Equal(200, Run(Arrange(), 9001, 9201, 9101).Status);

        [Fact] public void AnotherCustomersUnit_Is404_FoundFalse()
        {
            var (s, b, _) = Run(Arrange(), 9001, 5555, 5000);
            Assert.Equal(404, s); Assert.False(b.GetProperty("found").GetBoolean()); Assert.False(b.TryGetProperty("unit", out _));
        }

        [Fact] public void NonexistentUnit_GivesTheIdenticalAnswerAsAnotherCustomersUnit()
        {
            var c = Arrange();
            var other = Run(c, 9001, 5555, 5000); var missing = Run(Arrange(), 9001, 424242, 9100);
            Assert.Equal(other.Status, missing.Status);
            Assert.Equal(other.Body.GetRawText(), missing.Body.GetRawText());
        }

        [Fact] public void RightCustomerAndUnit_ButAnotherCustomersLead_IsRefused()
            => Assert.Equal(404, Run(Arrange(), 9001, 9200, 8000).Status);   // 8000 belongs to customer 7777

        [Fact] public void LeadOfAnotherUnit_IsRefused()
            => Assert.Equal(404, Run(Arrange(), 9001, 9200, 9101).Status);   // 9101 is the lead for unit 9201

        [Fact] public void NotSoldOrContractLead_IsRefused_EvenForTheRealBuyer()
            => Assert.Equal(404, Run(Arrange(), 9001, 9200, 9999).Status);   // Hot lead

        [Fact] public void CancelledLead_IsRefused()
            => Assert.Equal(404, Run(Arrange(), 7777, 9200, 8000).Status);

        [Fact] public void NonBuyerRelationship_IsRefused()
        {
            var c = Arrange(); DbHolder.Current.LC.Single(x => x.LeadID == 9100).CustomerType = (int)CustomerType.Seller;
            Assert.Equal(404, Run(c, 9001, 9200, 9100).Status);
        }

        [Fact] public void LeadIsNeverGuessed_ItIsRequired()
            => Assert.Equal(400, Run(Arrange(), 9001, 9200, 0).Status);

        // ---- missing data / zeros / sale protection ----
        [Fact] public void UnmappedFields_AreNull_NeverInvented()
        {
            var u = Run(Arrange(), 9001, 9200, 9100).Body.GetProperty("unit");
            Assert.Equal(JsonValueKind.Null, u.GetProperty("bedrooms").ValueKind);
            Assert.Equal(JsonValueKind.Null, u.GetProperty("project").GetProperty("completionPercentage").ValueKind);
        }

        [Fact] public void Sale_IsNeverReturned_WithoutTheOperatorSwitch_EvenWhenAsked()
            => Assert.False(Run(Arrange(), 9001, 9200, 9100, sale: true).Body.GetProperty("unit").TryGetProperty("sale", out _));

        [Fact] public void Sale_IsOmitted_WhileUnmapped_EvenWithSwitchOn()
        {
            var c = Arrange(); System.Configuration.ConfigurationManager.AppSettings["TicketingReleaseSale"] = "true";
            Assert.False(Run(c, 9001, 9200, 9100, sale: true).Body.GetProperty("unit").TryGetProperty("sale", out _));
        }

        [Fact] public void SaleOfAnotherCustomer_NeverReachesTheSaleBuilder()
        {
            var c = Arrange(); System.Configuration.ConfigurationManager.AppSettings["TicketingReleaseSale"] = "true";
            var (s, b, _) = Run(c, 9001, 5555, 5000, sale: true);
            Assert.Equal(404, s); Assert.False(b.TryGetProperty("unit", out _));
        }

        // ---- failure ----
        [Fact] public void DatabaseFailure_Is500_Json_WithoutTheExceptionMessage_AndIsLogged()
        {
            var c = Arrange(); DbHolder.Throw = true;
            var (s, b, _) = Run(c, 9001, 9200, 9100);
            Assert.Equal(500, s);
            Assert.DoesNotContain("db down", b.GetRawText());
            Assert.Contains("GetUnitDetails", CRMHelpers.Logged);
        }
    }
}
