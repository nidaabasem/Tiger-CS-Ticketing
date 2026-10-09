// Hand-written stand-ins for the CRM types the insertion file touches.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace System.Configuration
{
    public static class ConfigurationManager
    {
        public static NameValueCollection AppSettings { get; } = new NameValueCollection();
    }
}

namespace System.Web
{
    public enum HttpCacheability { NoCache }
    public class HttpCachePolicy { public void SetCacheability(HttpCacheability c) { } public void SetNoStore() { } }
    public class HttpResponseBase
    {
        public int StatusCode { get; set; } = 200;
        public bool TrySkipIisCustomErrors { get; set; }
        public bool SuppressFormsAuthenticationRedirect { get; set; }
        public HttpCachePolicy Cache { get; } = new HttpCachePolicy();
    }
    public class HttpRequestBase { public NameValueCollection Headers { get; } = new NameValueCollection(); }
}

namespace System.Web.Mvc
{
    public enum JsonRequestBehavior { AllowGet, DenyGet }
    public abstract class ActionResult { }
    public class JsonResult : ActionResult { public object Data; public JsonRequestBehavior Behavior; }
    public class HttpGetAttribute : Attribute { }
    public class HttpPostAttribute : Attribute { }
    public class AllowAnonymousAttribute : Attribute { }
    public class Controller
    {
        public System.Web.HttpRequestBase Request { get; set; } = new System.Web.HttpRequestBase();
        public System.Web.HttpResponseBase Response { get; set; } = new System.Web.HttpResponseBase();
        protected JsonResult Json(object data, JsonRequestBehavior b) => new JsonResult { Data = data, Behavior = b };
    }
}

namespace CRM_Performance.Models
{
    public enum CustomerType { Buyer = 1, Seller = 2 }
    public enum LeadStatus { New = 1, Hot = 2, Reserved = 3, Contract = 4, Sold = 8, Cancelled = 9 }

    public class tblLeadCustomers { public long LeadID; public long CustomerID; public int CustomerType; public long? UnitID; }
    public class tblLeads { public long ID; public int Status; }
    public class tblUnits { public long ID; public string Number; public long ProjectID; }
    public class tblProjects { public long ID; public string Name; }

    public class FakeDb : IDisposable
    {
        public List<tblLeadCustomers> LC = new List<tblLeadCustomers>();
        public List<tblLeads> L = new List<tblLeads>();
        public List<tblUnits> U = new List<tblUnits>();
        public List<tblProjects> P = new List<tblProjects>();
        public IQueryable<tblLeadCustomers> tblLeadCustomers => LC.AsQueryable();
        public IQueryable<tblLeads> tblLeads => L.AsQueryable();
        public IQueryable<tblUnits> tblUnits => U.AsQueryable();
        public IQueryable<tblProjects> tblProjects => P.AsQueryable();
        public void Dispose() { }
    }

    public class DbHolder : IDisposable
    {
        public FakeDb DBEntity => Current;
        public static FakeDb Current = new FakeDb();
        public static bool Throw;
        public void Dispose() { }
    }

    public class dbConnection
    {
        public FakeDb DBEntity { get { if (DbHolder.Throw) throw new InvalidOperationException("db down"); return DbHolder.Current; } }
    }

    public static class CRMHelpers { public static List<string> Logged = new List<string>(); public static void LogError(Exception ex, string where) { Logged.Add(where); } }
}

namespace CRM_Performance.Controllers
{
    // Verbatim copy of the existing helper in the supplied controller.
    public partial class TicketingSystemController : System.Web.Mvc.Controller
    {
        private static bool CustomerDocumentsKeyMatches(string supplied, string expected)
        {
            if (string.IsNullOrWhiteSpace(supplied)) return false;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var left = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(supplied));
                var right = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(expected));
                var difference = 0;
                for (var i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
                return difference == 0;
            }
        }
    }
}
