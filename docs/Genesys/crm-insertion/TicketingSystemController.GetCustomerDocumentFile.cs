// =====================================================================
// Tiger CRM (CRM_Performance) - TicketingSystemController.GetCustomerDocumentFile
//
// INSERTION-READY, NOT COMPILED AGAINST THE REAL CRM PROJECT. The CRM source is not in the
// TigerCS repository, so every spot that depends on CRM entities/methods this file could not see
// is marked "ADAPT:" - fill those from the code GetCustomerDocuments already uses. Nothing here
// invents a column: GetAttachments(...) and GetAttachmentByGUIDAsync(guid) are the existing CRM
// methods named by the CRM team.
//
// WHY: GetCustomerDocuments returns a FileURL that points at a PRIVATE S3 object (or a DB row).
// TigerCS cannot fetch it (403), and the objects must stay private. This action lets TigerCS ask
// CRM for the bytes instead; CRM uses its own storage credentials and nothing about S3 (URL,
// key, bucket, credential) ever leaves CRM.
//
// HOW TO APPLY: as for GetUnitDetails - make TicketingSystemController `partial` and add this
// file, or paste the members between the markers into the class. Reuses the existing private
// helper CustomerDocumentsKeyMatches.
//
// Syntax is deliberately C# 5 compatible (no ?., string interpolation, nameof).
// =====================================================================
using CRM_Performance.Models;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

namespace CRM_Performance.Controllers
{
    public partial class TicketingSystemController
    {
        // ------------------- BEGIN GetCustomerDocumentFile -------------------

        /// <summary>
        /// POST /TicketingSystem/GetCustomerDocumentFile
        /// Header: X-SECRET-KEY (AppSettings["TicketingSecretKey"]).  Body (JSON):
        ///   { "CustomerID": 9001, "LeadID": 12345, "DocumentType": "TigerContract", "AttachmentId": 5001 }
        /// Called only by TigerCS Ticketing (server to server), only for an AttachmentId that
        /// GetCustomerDocuments just listed for that customer and lead.
        ///
        /// 200  raw file bytes. Content-Type = the file's real type (application/pdf), Content-Disposition
        ///      attachment with a SAFE file name, Content-Length, X-Content-SHA256 = hex SHA-256 of the body.
        /// 400  malformed request or unsupported DocumentType (JSON)
        /// 401  missing/wrong X-SECRET-KEY (JSON, never a login redirect)
        /// 404  JSON. ONE answer for: no such attachment, attachment of another customer, another lead,
        ///      another document type, inactive attachment, or bytes not found in S3/database - so the
        ///      caller learns nothing about attachments it does not own.
        /// 503  integration not configured / storage temporarily unavailable (JSON)
        /// </summary>
        [HttpPost]
        [AllowAnonymous] // Authentication is explicitly enforced by the key below, never by the CRM Session/AllowEdit.
        public async Task<ActionResult> GetCustomerDocumentFile(long CustomerID = 0, long LeadID = 0, string DocumentType = null, long AttachmentId = 0)
        {
            Response.Cache.SetCacheability(HttpCacheability.NoCache);
            Response.Cache.SetNoStore();

            // ---- 1. Service authentication (JSON, never a login redirect) ----
            var expectedKey = ConfigurationManager.AppSettings["TicketingSecretKey"];
            if (string.IsNullOrWhiteSpace(expectedKey))
                return DocumentFileJson(503, new { success = false, message = "Ticketing integration is not configured." });

            if (!CustomerDocumentsKeyMatches(Request.Headers["X-SECRET-KEY"], expectedKey))
                return DocumentFileJson(401, new { success = false, message = "Invalid secret key." });

            // ---- 2. Input. Every field is mandatory; the attachment is never looked up by id alone. ----
            // Same names as GetCustomerDocuments: ReservationForm (4), TigerContract (5), RegistrationReceipt (6).
            // Layout is NOT served here: it is the unit's unitplan field, not an attachment.
            int attachmentDocumentType;
            if (CustomerID <= 0 || LeadID <= 0 || AttachmentId <= 0 || !TryDocumentFileType(DocumentType, out attachmentDocumentType))
                return DocumentFileJson(400, new { success = false, message = "CustomerID, LeadID, DocumentType (ReservationForm, TigerContract, RegistrationReceipt) and AttachmentId are required." });

            try
            {
                using (var db = new dbConnection().DBEntity)
                {
                    // ---- 3. Authorise BEFORE loading any bytes ----
                    // ADAPT: this MUST be the same ownership rule GetCustomerDocuments uses to list a customer's
                    // documents (customer is a Buyer of this lead, lead Sold/Contract ... exactly as there), so a
                    // document can never be downloaded that would not have been listed. Pin ALL of:
                    //   customer == CustomerID, lead == LeadID, attachment.ID == AttachmentId,
                    //   attachment.DocumentType == attachmentDocumentType (4/5/6), attachment active (Status),
                    //   attachment.ParentID/ParentType == this lead (the same ParentType value GetCustomerDocuments uses).
                    // Equivalent, using the existing method the CRM team named:
                    //   var listed = GetAttachments(LeadID /*parentID*/, <lead ParentType>, <objectID>, <objectType>, attachmentDocumentType);
                    //   var attachment = listed.FirstOrDefault(a => a.ID == AttachmentId);   // and the buyer check above
                    var attachment = FindOwnedAttachment(db, CustomerID, LeadID, attachmentDocumentType, AttachmentId);
                    if (attachment == null)
                        return DocumentFileJson(404, new { success = true, found = false, message = "Document not found for this customer and unit." });

                    // ---- 4. Resolve the GUID from the AUTHORISED record only (never from the request) ----
                    // ADAPT: the property that holds the attachment's GUID (the value GetAttachmentByGUIDAsync takes).
                    Guid guid = attachment.GUID;

                    // ---- 5. Bytes from the existing method: private S3 with the CRM's own credentials, DB fallback ----
                    // ADAPT: use the actual return type of GetAttachmentByGUIDAsync (bytes, and file name / content type if it returns them).
                    byte[] bytes = null;
                    string storedName = null;
                    var file = await GetAttachmentByGUIDAsync(guid);
                    if (file != null)
                    {
                        bytes = file.Bytes;          // ADAPT
                        storedName = file.FileName;  // ADAPT (may be null)
                    }

                    if (bytes == null || bytes.Length == 0)
                        return DocumentFileJson(404, new { success = true, found = false, message = "Document not found for this customer and unit." });

                    // ---- 6. Binary response: real MIME type, safe file name, integrity header ----
                    var contentType = LooksLikePdf(bytes) ? "application/pdf" : "application/octet-stream";
                    var safeName = SafeDocumentFileName(storedName ?? attachment.Name, contentType);

                    Response.StatusCode = 200;
                    Response.Headers["X-Content-SHA256"] = Sha256Hex(bytes);
                    Response.Headers["X-Content-Type-Options"] = "nosniff";
                    Response.AddHeader("Content-Disposition", "attachment; filename=\"" + safeName + "\"");
                    return File(bytes, contentType);
                }
            }
            catch (Exception ex)
            {
                // Never echo exception text: it can name buckets, keys or connection strings.
                System.Diagnostics.Trace.TraceError("GetCustomerDocumentFile failed: " + ex.GetType().Name);
                return DocumentFileJson(503, new { success = false, message = "The document store is temporarily unavailable." });
            }
        }

        // ADAPT: implement with the same joins/filters GetCustomerDocuments uses (see step 3). Return null for ANY mismatch.
        private dynamic FindOwnedAttachment(dynamic db, long customerId, long leadId, int attachmentDocumentType, long attachmentId)
        {
            throw new NotImplementedException("Reuse GetCustomerDocuments' ownership + tblAttachments query, filtered to AttachmentId.");
        }

        private static bool TryDocumentFileType(string name, out int attachmentType)
        {
            attachmentType = 0;
            if (string.Equals(name, "ReservationForm", StringComparison.OrdinalIgnoreCase)) attachmentType = 4;
            else if (string.Equals(name, "TigerContract", StringComparison.OrdinalIgnoreCase)) attachmentType = 5;
            else if (string.Equals(name, "RegistrationReceipt", StringComparison.OrdinalIgnoreCase)) attachmentType = 6;
            return attachmentType != 0;
        }

        private static bool LooksLikePdf(byte[] b)
        {
            return b.Length >= 5 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46 && b[4] == 0x2D; // %PDF-
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        /// <summary>A header-safe file name: no path, quotes, semicolons or control characters; extension matches the real type.</summary>
        private static string SafeDocumentFileName(string name, string contentType)
        {
            var n = (name ?? "document").Replace('\\', '/');
            n = n.Substring(n.LastIndexOf('/') + 1);
            var chars = n.Where(c => c >= ' ' && c != '"' && c != ';' && c != ',' && c != '\u007f').ToArray();
            n = new string(chars).Trim().TrimEnd('.');
            var dot = n.LastIndexOf('.');
            if (dot > 0) n = n.Substring(0, dot);
            if (n.Length == 0) n = "document";
            if (n.Length > 120) n = n.Substring(0, 120);
            return n + (contentType == "application/pdf" ? ".pdf" : ".bin");
        }

        private JsonResult DocumentFileJson(int statusCode, object body)
        {
            Response.StatusCode = statusCode;
            Response.TrySkipIisCustomErrors = true;
            Response.SuppressFormsAuthenticationRedirect = true;
            return Json(body, JsonRequestBehavior.DenyGet);
        }

        // -------------------- END GetCustomerDocumentFile --------------------
    }
}
