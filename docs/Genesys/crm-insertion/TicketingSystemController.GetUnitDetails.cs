// =====================================================================
// Tiger CRM (CRM_Performance) - TicketingSystemController.GetUnitDetails
//
// INSERTION-READY, NOT COMPILED AGAINST THE REAL CRM PROJECT.
// Written against the controller source supplied on 2026-10-09
// (GetBuyerByPhone / GetCustomerDocuments). Only entity/property names that
// appear in that file are used. See CRM-GetUnitDetails-Field-Mapping.md.
//
// HOW TO APPLY: either (a) change the declaration of the existing
// `public class TicketingSystemController : Controller` to
// `public partial class TicketingSystemController : Controller` and add this
// file to the project, or (b) paste the members between the markers into the
// class. It reuses the existing private helper CustomerDocumentsKeyMatches.
//
// Syntax is deliberately C# 5 compatible (no ?., string interpolation, nameof).
// =====================================================================
using CRM_Performance.Models;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Web;
using System.Web.Mvc;

namespace CRM_Performance.Controllers
{
    public partial class TicketingSystemController
    {
        // ----------------------- BEGIN GetUnitDetails -----------------------

        /// <summary>
        /// GET /TicketingSystem/GetUnitDetails?customerId=&amp;unitId=&amp;leadId=[&amp;includeSale=true]
        /// Header: X-SECRET-KEY (AppSettings["TicketingSecretKey"]).
        /// Called only by TigerCS Ticketing (server to server).
        /// </summary>
        [HttpGet]
        [AllowAnonymous] // Authentication is explicitly enforced by the key below, never by the CRM Session/AllowEdit.
        public ActionResult GetUnitDetails(long customerId = 0, long unitId = 0, long leadId = 0, bool includeSale = false)
        {
            Response.Cache.SetCacheability(HttpCacheability.NoCache);
            Response.Cache.SetNoStore();

            // ---- 1. Service authentication (JSON, never a login redirect) ----
            var expectedKey = ConfigurationManager.AppSettings["TicketingSecretKey"];
            if (string.IsNullOrWhiteSpace(expectedKey))
                return UnitDetailsJson(503, new { success = false, found = false, message = "Ticketing integration is not configured." });

            if (!CustomerDocumentsKeyMatches(Request.Headers["X-SECRET-KEY"], expectedKey))
                return UnitDetailsJson(401, new { success = false, found = false, message = "Invalid secret key." });

            // ---- 2. Input. leadId is mandatory: the sale is never "the latest booking". ----
            if (customerId <= 0 || unitId <= 0 || leadId <= 0)
                return UnitDetailsJson(400, new { success = false, found = false, message = "customerId, unitId and leadId are required." });

            try
            {
                using (var db = new dbConnection().DBEntity)
                {
                    // ---- 3. Ownership + lead binding ----
                    // Same relationship and Buyer rule as GetBuyerByPhone
                    // (tblLeadCustomers -> tblLeads / tblUnits -> tblProjects), pinned to
                    // the requested customer, unit AND lead, and restricted to Sold/Contract.
                    // NOTE: the supplied GetBuyerByPhone has its Sold/Contract filter commented
                    // out (it only excludes Cancelled). This action is deliberately strict.
                    var owned = (
                        from lc in db.tblLeadCustomers
                        join l in db.tblLeads on lc.LeadID equals l.ID
                        join u in db.tblUnits on lc.UnitID equals (long?)u.ID
                        join p in db.tblProjects on u.ProjectID equals p.ID
                        where lc.CustomerID == customerId
                            && lc.LeadID == leadId
                            && lc.UnitID == unitId
                            && lc.CustomerType == (int)CustomerType.Buyer
                            && (l.Status == (int)LeadStatus.Sold || l.Status == (int)LeadStatus.Contract)
                        select new
                        {
                            LeadId = l.ID,
                            UnitId = u.ID,
                            ProjectId = p.ID
                        }).FirstOrDefault();

                    // Same answer for an unknown unit, another customer's unit, a different
                    // lead of the same unit, and a non-Sold/Contract lead.
                    if (owned == null)
                        return UnitDetailsJson(404, new { success = true, found = false, message = "Unit not found for this customer." });

                    // ---- 4. Unit / project facts ----
                    // Every member below is part of the agreed contract. A member is filled
                    // ONLY where the supplied source shows a column for it; null = not recorded
                    // or not mapped yet. NEEDS: markers name the exact information still missing.
                    var project = new Dictionary<string, object>
                    {
                        { "address", null },                 // NEEDS: tblProjects property holding the customer-facing address/location
                        { "status", null },                  // NEEDS: tblProjects project-status property (+ its enum/labels)
                        { "expectedHandoverDate", null },    // NEEDS: tblProjects planned HANDOVER date property
                        { "actualHandoverDate", null },      // NEEDS: tblProjects actual HANDOVER date property
                        { "description", null },             // NEEDS: tblProjects customer-facing description property (not internal notes)
                        { "amenities", null },               // NEEDS: amenity table/property, if any
                        { "completionPercentage", null },    // NEEDS: tblProjects construction-completion percentage property
                        { "expectedCompletionDate", null },  // NEEDS: tblProjects planned COMPLETION date property (not handover)
                        { "actualCompletionDate", null }     // NEEDS: tblProjects actual COMPLETION date property (not handover)
                    };

                    var unit = new Dictionary<string, object>
                    {
                        { "unitTypeName", null },            // NEEDS: label for tblUnits.Type (enum name or lookup table); Ticketing already has the numeric code
                        { "towerName", null },               // NEEDS: tblUnits tower/building property or relation
                        { "bedrooms", null },                // NEEDS: tblUnits bedrooms property
                        { "area", null },                    // NEEDS: which of SuitesArea/BalconyArea/NetArea/CommonArea/GrossArea is the customer-facing area
                        { "areaUnit", null },                // NEEDS: measurement unit of those areas (sqft/sqm); no column seen
                        { "parking", null },                 // NEEDS: parking table/property; null = unknown, [] only when known to be none
                        { "expectedHandoverDate", null },    // NEEDS: tblUnits (or tblLeads) planned HANDOVER date property
                        { "actualHandoverDate", null },      // NEEDS: tblUnits (or tblLeads) actual HANDOVER date property
                        { "project", project }
                    };

                    // ---- 5. Sale (private) ----
                    // Released only when (a) TigerCS asks (includeSale=true - it sends that
                    // ONLY after validating a server-recorded verification session; CRM has no
                    // session of its own, so the secret key is the trust anchor and the flag is
                    // never honoured without it) and (b) the CRM operator has switched it on.
                    if (includeSale && string.Equals(ConfigurationManager.AppSettings["TicketingReleaseSale"], "true", StringComparison.OrdinalIgnoreCase))
                    {
                        var sale = BuildSaleForLead(owned.LeadId);
                        if (sale != null)
                            unit["sale"] = sale;
                    }

                    return UnitDetailsJson(200, new { success = true, found = true, message = (string)null, unit = unit });
                }
            }
            catch (Exception ex)
            {
                CRMHelpers.LogError(ex, "GetUnitDetails");
                return UnitDetailsJson(500, new { success = false, found = false, message = "Unable to retrieve unit details." });
            }
        }

        /// <summary>
        /// The sale of exactly the lead already proven to belong to the customer and unit.
        /// Returns null (member omitted) until the columns below are confirmed - nothing is guessed.
        /// </summary>
        private static object BuildSaleForLead(long leadId)
        {
            // NEEDS (tblLeads, or the booking/contract table it points to), read WHERE ID == leadId:
            //   soldPrice        - the price THIS customer agreed (NOT tblUnits list/asking price)
            //   registrationCost - the registration fee AMOUNT stored for the sale
            //                      (NOT a percentage, NOT the RegistrationReceipt attachment, NOT computed)
            //   currency         - ISO-4217 code recorded for the amounts (null if not stored)
            // Open the context here (using (var db = new dbConnection().DBEntity)) once the columns are known.
            // Expected return once mapped (0 is a real value; keep decimals; null when not stored):
            //   return new { leadId = leadId, soldPrice = (decimal?)x.<Price>, registrationCost = (decimal?)x.<Fee>, currency = (string)x.<Currency> };
            return null;
        }

        private JsonResult UnitDetailsJson(int statusCode, object body)
        {
            Response.StatusCode = statusCode;
            Response.TrySkipIisCustomErrors = true;
            Response.SuppressFormsAuthenticationRedirect = true;
            // NB: CustomerDocumentsJson uses DenyGet (it is a POST action); this one is a GET.
            return Json(body, JsonRequestBehavior.AllowGet);
        }

        // ------------------------ END GetUnitDetails ------------------------
    }
}
