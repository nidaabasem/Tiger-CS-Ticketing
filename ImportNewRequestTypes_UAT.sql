/*
    ImportNewRequestTypes_UAT.sql
    =============================

    UAT PREPARATION ONLY — DO NOT RUN AGAINST PRODUCTION.

    Imports the 35 proposed NEW request types of the business-review workbook
    docs/business-review/TigerCS_New_Request_Types_Business_Review.xlsx into an
    EXISTING UAT database, for business review. Every row's Business Decision
    is still blank, so nothing here is approved configuration.

    It is the SQL twin of NewRequestTypesImporter (the development seed's
    path for fresh databases) and applies exactly the same rules. Its data
    block is RENDERED from the same C# catalog; the test suite fails if the
    two ever differ (NewRequestTypesImportTests). See
    docs/New-Request-Types-UAT-Import.md for the full mapping report.

    WHAT IT CREATES, per workbook row that can be imported
    ------------------------------------------------------
    * one logical Workflow whose Code IS the Request Code (e.g. CS-GEN-001) —
      the stable business key, since RequestTypes has no code column;
    * its version 1 (WorkflowTemplates, same code) with the structured steps
      translated from "Proposed Workflow"; Published when every hand-off
      destination department exists, otherwise left as a DRAFT;
    * one RequestTypes row, IsActive = 0, in the owning department;
    * one RequestTypeSlaPolicies row (resolution in business days) where the
      workbook gives a number.

    WHAT IT NEVER DOES
    ------------------
    * never UPDATEs or DELETEs anything — existing request types, workflows,
      approval requirements (ReopenApproval included), department settings
      and tickets are untouched;
    * never creates a department (Leasing Customer Services, Sales, Admin
      Sales, Legal, HR, Marketing are resolved by exact name or reported);
    * never creates an approval requirement (no workbook approval role maps
      exactly onto the supported approval model — see the report);
    * never invents an SLA number ("Same business day", "Based on severity").

    MATCHING / IDEMPOTENCY (first rule that applies wins)
    -----------------------------------------------------
      owning department not found (by code, then exact name) → SkippedOwningDepartmentMissing
      Workflow with Code = Request Code + request type on it  → AlreadyImported (left as is)
      Workflow / version already using the Request Code        → SkippedCodeInUse
      same-named request type already in owning department     → SkippedExistingRequestType
      otherwise                                                → Created / CreatedWithDraftWorkflow
    A second run therefore creates nothing.

    ACTIVATION (after the business decides)
    ---------------------------------------
    Activate approved rows in Administration → Request Types. Administration
    refuses to activate a request type whose workflow has no Published
    version, so a DRAFT row cannot go live until its destination is decided
    and its workflow published in the Workflow Designer. After activating,
    re-run ConfigureReopenApprovalRequirements.sql if the approved "every
    reopenable type gets ReopenApproval" rule should cover them — that script
    only targets ACTIVE request types, and this one does not touch it.

    DRY RUN
    -------
    Set @CommitChanges = 0 below: everything is applied inside the
    transaction, the reconciliation is printed, then it is rolled back.

    RUNNING IT
    ----------
        sqlcmd -S <server> -d <database> -U <user> -P <password> -C -b -I \
               -i ImportNewRequestTypes_UAT.sql

    -I (QUOTED_IDENTIFIER ON) is required: sqlcmd defaults it OFF, and SQL
    Server then refuses DML against tables carrying filtered indexes
    (WorkflowTemplates has two). Same rule as the repository's other scripts.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

DECLARE @CommitChanges bit = 1;   -- 0 = dry run (apply, report, roll back)

-------------------------------------------------------------------------------
-- 0. Pre-flight.
-------------------------------------------------------------------------------

IF OBJECT_ID(N'[Workflows]', N'U') IS NULL
    OR OBJECT_ID(N'[WorkflowTemplates]', N'U') IS NULL
    OR OBJECT_ID(N'[WorkflowTemplateSteps]', N'U') IS NULL
    OR OBJECT_ID(N'[RequestTypes]', N'U') IS NULL
    OR OBJECT_ID(N'[RequestTypeSlaPolicies]', N'U') IS NULL
    OR OBJECT_ID(N'[Departments]', N'U') IS NULL
    OR COL_LENGTH(N'[WorkflowTemplates]', N'Status') IS NULL
BEGIN
    THROW 50001, N'Schema not found: apply EF migrations (through the Workflow Designer versioning migration) before running this script.', 1;
END;

IF (SELECT COUNT(*) FROM [Priorities] WHERE [PriorityId] IN (2, 3, 4)) <> 3
BEGIN
    THROW 50002, N'Priorities High (2), Medium (3) and Low (4) must exist.', 1;
END;

-------------------------------------------------------------------------------
-- 1. The workbook rows (rendered from NewRequestTypesBusinessReview).
--    Kind: 1 Start, 2 Queue/Assignment, 5 Department Work, 8 Resolve,
--          9 Close, 11 Maintenance Dependency.
--    DefaultPriorityId: 2 High, 3 Medium (workbook "Normal"), 4 Low.
-------------------------------------------------------------------------------

DECLARE @Rows TABLE (
    [Ordinal]             int            NOT NULL,
    [RequestCode]         nvarchar(24)   NOT NULL PRIMARY KEY,
    [OwnerDepartmentName] nvarchar(100)  NOT NULL,
    [OwnerDepartmentCode] nvarchar(10)   NULL,
    [Name]                nvarchar(100)  NOT NULL,
    [WorkflowName]        nvarchar(100)  NOT NULL,
    [WorkflowDescription] nvarchar(500)  NOT NULL,
    [DefaultPriorityId]   tinyint        NOT NULL,
    [AllowReopen]         bit            NOT NULL,
    [RequiredFieldsJson]  nvarchar(2000) NULL,
    [ResolutionDays]      int            NULL);

DECLARE @Steps TABLE (
    [RequestCode] nvarchar(24)  NOT NULL,
    [Sequence]    tinyint       NOT NULL,
    [Name]        nvarchar(100) NOT NULL,
    [Kind]        tinyint       NOT NULL,
    [IsOptional]  bit           NOT NULL,
    PRIMARY KEY ([RequestCode], [Sequence]));

DECLARE @Destinations TABLE (
    [RequestCode]    nvarchar(24)  NOT NULL,
    [Ordinal]        int           NOT NULL,
    [DepartmentName] nvarchar(100) NOT NULL,
    [DepartmentCode] nvarchar(10)  NULL,
    PRIMARY KEY ([RequestCode], [Ordinal]));

-- <generated-data> (rendered from NewRequestTypesBusinessReview; do not edit by hand)
INSERT INTO @Rows ([Ordinal], [RequestCode], [OwnerDepartmentName], [OwnerDepartmentCode], [Name], [WorkflowName], [WorkflowDescription], [DefaultPriorityId], [AllowReopen], [RequiredFieldsJson], [ResolutionDays]) VALUES
(1, N'CS-GEN-001', N'Customer Service', N'CS', N'General Inquiry', N'CS-GEN-001 General Inquiry', N'New request type under business review (UAT). Request Group: General Inquiries. General customer inquiry that does not belong to a specialized request type. Proposed workflow (source text): CS Queue → Agent → Resolve → Close Required documents: None.', 3, 1, N'["Customer","Project/Unit if applicable","Description"]', 1),
(2, N'CS-GEN-002', N'Customer Service', N'CS', N'Office Hours / Contact Information', N'CS-GEN-002 Office Hours / Contact Information', N'New request type under business review (UAT). Request Group: General Inquiries. Office hours, contact details and general service information. Proposed workflow (source text): CS Queue → Agent → Resolve → Close Required documents: None.', 4, 1, N'["Customer/Contact","Description"]', NULL),
(3, N'CS-GEN-003', N'Customer Service', N'CS', N'Construction Update', N'CS-GEN-003 Construction Update', N'New request type under business review (UAT). Request Group: General Inquiries. Customer request for project construction/progress update. Proposed workflow (source text): CS Queue → Agent → Obtain update if needed → Resolve → Close Required documents: None.', 3, 1, N'["Customer","Project","Unit if applicable"]', 1),
(4, N'CS-CMP-001', N'Customer Service', N'CS', N'Complaint', N'CS-CMP-001 Complaint', N'New request type under business review (UAT). Request Group: Complaints & Feedback. Formal customer complaint requiring follow-up and possible escalation. Proposed workflow (source text): CS Queue → Agent → Escalate if needed → Resolve → Close Required documents: Supporting evidence if available.', 2, 1, N'["Customer","Project/Unit","Complaint details"]', 2),
(5, N'CS-CMP-002', N'Customer Service', N'CS', N'Feedback / Suggestion', N'CS-CMP-002 Feedback / Suggestion', N'New request type under business review (UAT). Request Group: Complaints & Feedback. Customer feedback, suggestion or service improvement request. Proposed workflow (source text): CS Queue → Agent → Record/route → Resolve → Close Required documents: None.', 4, 1, N'["Customer","Feedback details"]', 1),
(6, N'REG-NOC-001', N'Customer Service', N'CS', N'NOC for Resale', N'REG-NOC-001 NOC for Resale', N'New request type under business review (UAT). Request Group: NOC. Request to issue NOC for property resale. Proposed workflow (source text): CS Queue → CS Agent →Acounting→CS Agentt → Resolve → Close Required documents: Required resale NOC documents.', 3, 1, N'["Customer","Project","Unit","Request details"]', 2),
(7, N'REG-NOC-002', N'Customer Service', N'CS', N'NOC for Golden Visa', N'REG-NOC-002 NOC for Golden Visa', N'New request type under business review (UAT). Request Group: NOC. Request to issue NOC/supporting letter for Golden Visa. Proposed workflow (source text): CS Queue → CS Agent →Acounting→CS Agentt → Resolve → Close Required documents: Required Golden Visa/NOC documents.', 3, 1, N'["Customer","Project","Unit"]', 2),
(8, N'REG-NOC-003', N'Customer Service', N'CS', N'NOC for Mortgage', N'REG-NOC-003 NOC for Mortgage', N'New request type under business review (UAT). Request Group: NOC. Request to issue NOC for mortgage-related processing. Proposed workflow (source text): CS Queue → CS Agent →Acounting→CS Agentt → Resolve → Close Required documents: Required mortgage/NOC documents.', 3, 1, N'["Customer","Project","Unit"]', 2),
(9, N'REG-CON-001', N'Registration', N'REG', N'SPA / Contract Inquiry', N'REG-CON-001 SPA / Contract Inquiry', N'New request type under business review (UAT). Request Group: Contracts / DLD. Inquiry regarding Sales & Purchase Agreement or contract details. Proposed workflow (source text): Registration Queue → Agent → Review → Resolve → Close Required documents: Contract if needed.', 3, 1, N'["Customer","Project","Unit","Contract reference"]', 1),
(10, N'REG-DLD-001', N'Registration', N'REG', N'Ownership Transfer / Title Deed Inquiry', N'REG-DLD-001 Ownership Transfer / Title Deed Inquiry', N'New request type under business review (UAT). Request Group: Contracts / DLD. Inquiry regarding ownership transfer, title deed or pre-title deed. Proposed workflow (source text): Registration Queue → Agent → Review DLD/registration status → Resolve → Close Required documents: Relevant ownership/title documents if needed.', 3, 1, N'["Customer","Project","Unit"]', 1),
(11, N'COL-PAY-001', N'Collections', N'COL', N'Payment / Outstanding Balance Inquiry', N'COL-PAY-001 Payment / Outstanding Balance Inquiry', N'New request type under business review (UAT). Request Group: Payments & Collection. Inquiry about customer payments, due amounts or outstanding balance. Proposed workflow (source text): Collections Queue → Agent → Review account → Resolve → Close Required documents: Payment proof if applicable.', 3, 1, N'["Customer","Project","Unit"]', 1),
(12, N'COL-PAY-002', N'Collections', N'COL', N'Returned Cheque', N'COL-PAY-002 Returned Cheque', N'New request type under business review (UAT). Request Group: Payments & Collection. Follow-up for a returned/bounced cheque. Proposed workflow (source text): Collections Queue → Agent → Follow-up → Escalate if needed → Resolve → Close Required documents: Cheque copy / bank return document.', 2, 1, N'["Customer","Project","Unit","Cheque details"]', 1),
(13, N'COL-PAY-003', N'Collections', N'COL', N'Cheque Collection', N'COL-PAY-003 Cheque Collection', N'New request type under business review (UAT). Request Group: Payments & Collection. Request or inquiry related to cheque collection. Proposed workflow (source text): Collections Queue → Agent → Confirm cheque/collection → Resolve → Close Required documents: Cheque details if applicable.', 3, 1, N'["Customer","Project","Unit","Cheque details"]', 1),
(14, N'COL-PAY-004', N'Collections', N'COL', N'Payment Cheque Inquiry', N'COL-PAY-004 Payment Cheque Inquiry', N'New request type under business review (UAT). Request Group: Payments & Collection. General inquiry regarding a payment cheque. Proposed workflow (source text): Collections Queue → Agent → Review → Resolve → Close Required documents: Cheque copy if required.', 3, 1, N'["Customer","Project","Unit","Cheque details"]', 1),
(15, N'HO-NOC-001', N'Customer Service', N'CS', N'NOC for Handover', N'HO-NOC-001 NOC for Handover', N'New request type under business review (UAT). Request Group: Handover. Request for NOC required before handover. Proposed workflow (source text): CS Queue →Agent→Acounting→ CS Agent → Handover Agent → Resolve → Close Required documents: Required clearance/NOC documents.', 3, 1, N'["Customer","Project","Unit"]', 2),
(16, N'HO-HND-001', N'Handover', N'HO', N'Coordinate Handover', N'HO-HND-001 Coordinate Handover', N'New request type under business review (UAT). Request Group: Handover. Coordinate and follow up the property handover process. Proposed workflow (source text): Handover Queue → Agent → Coordinate → Resolve → Close Required documents: Handover documents if applicable.', 3, 1, N'["Customer","Project","Unit"]', 1),
(17, N'HO-HND-002', N'Handover', N'HO', N'Schedule Handover Appointment', N'HO-HND-002 Schedule Handover Appointment', N'New request type under business review (UAT). Request Group: Handover. Schedule or reschedule a handover appointment. Proposed workflow (source text): Handover Queue → Agent → Confirm available slot → Resolve → Close Required documents: None.', 3, 1, N'["Customer","Project","Unit","Preferred date/time"]', 1),
(18, N'HO-HND-003', N'Handover', N'HO', N'Move-In / Move-Out', N'HO-HND-003 Move-In / Move-Out', N'New request type under business review (UAT). Request Group: Handover. Request related to move-in or move-out coordination for handed-over properties. Proposed workflow (source text): Handover Queue → Agent → Coordinate requirements → Resolve → Close Required documents: Required move-in/out documents if applicable.', 3, 1, N'["Customer","Project","Unit","Move date"]', 1),
(19, N'HO-HND-004', N'Handover', N'HO', N'Handover Maintenance Follow-up', N'HO-HND-004 Handover Maintenance Follow-up', N'New request type under business review (UAT). Request Group: Handover. Maintenance issue identified during or after handover requiring follow-up. Proposed workflow (source text): Handover Queue → Facilities Management if needed → Follow-up → Resolve → Close Required documents: Photos/supporting evidence if available.', 3, 1, N'["Customer","Project","Unit","Issue details"]', NULL),
(20, N'FM-MNT-001', N'Facilities Management', N'FM', N'Repair / Maintenance Request', N'FM-MNT-001 Repair / Maintenance Request', N'New request type under business review (UAT). Request Group: Maintenance. Repair or maintenance request for a property/unit. Proposed workflow (source text): FM Queue → Assign Agent/Technician → In Progress → Resolve → Close Required documents: Photos if available.', 3, 1, N'["Customer","Project","Unit","Issue type","Description"]', NULL),
(21, N'FM-UTL-001', N'Facilities Management', N'FM', N'Utilities Inquiry', N'FM-UTL-001 Utilities Inquiry', N'New request type under business review (UAT). Request Group: Maintenance. Inquiry or issue related to utilities. Proposed workflow (source text): FM Queue → Agent → Review/coordinate → Resolve → Close Required documents: Supporting document if applicable.', 3, 1, N'["Customer","Project","Unit","Utility type"]', 1),
(22, N'FM-COM-001', N'Facilities Management', N'FM', N'Common Area Maintenance', N'FM-COM-001 Common Area Maintenance', N'New request type under business review (UAT). Request Group: Maintenance. Maintenance issue in a shared/common area. Proposed workflow (source text): FM Queue → Agent/Technician → In Progress → Resolve → Close Required documents: Photos if available.', 3, 1, N'["Project/Building","Location","Issue details"]', NULL),
(23, N'FM-SVC-001', N'Facilities Management', N'FM', N'Service Charge Inquiry', N'FM-SVC-001 Service Charge Inquiry', N'New request type under business review (UAT). Request Group: Service Charge. Inquiry regarding service charges. Proposed workflow (source text): FM/Responsible Finance Queue → Agent → Review → Resolve → Close Required documents: Statement/invoice if applicable.', 3, 1, N'["Customer","Project","Unit"]', 1),
(24, N'LCS-TEN-001', N'Leasing Customer Services', NULL, N'Tenancy Contract', N'LCS-TEN-001 Tenancy Contract', N'New request type under business review (UAT). Request Group: Leasing. Request or inquiry related to tenancy contract. Proposed workflow (source text): Leasing CS Queue → Agent → Process/Review → Resolve → Close Required documents: Required tenancy documents.', 3, 1, N'["Customer/Tenant","Property/Unit"]', 2),
(25, N'LCS-EJR-001', N'Leasing Customer Services', NULL, N'Ejari', N'LCS-EJR-001 Ejari', N'New request type under business review (UAT). Request Group: Leasing. Request or inquiry related to Ejari registration. Proposed workflow (source text): Leasing CS Queue → Agent → Process/Review → Resolve → Close Required documents: Required Ejari documents.', 3, 1, N'["Customer/Tenant","Property/Unit"]', 2),
(26, N'LCS-BKG-001', N'Leasing Customer Services', NULL, N'Booking', N'LCS-BKG-001 Booking', N'New request type under business review (UAT). Request Group: Leasing. Leasing booking inquiry or request. Proposed workflow (source text): Leasing CS Queue → Agent → Confirm booking/details → Resolve → Close Required documents: Booking document if applicable.', 3, 1, N'["Customer/Tenant","Property/Unit"]', 1),
(27, N'LCS-MOV-001', N'Leasing Customer Services', NULL, N'Move-In / Move-Out', N'LCS-MOV-001 Move-In / Move-Out', N'New request type under business review (UAT). Request Group: Leasing. Leasing move-in or move-out coordination. Proposed workflow (source text): Leasing CS Queue → Agent → Coordinate → Resolve → Close Required documents: Required move-in/out documents if applicable.', 3, 1, N'["Customer/Tenant","Property/Unit","Move date"]', 1),
(28, N'LCS-CHK-001', N'Leasing Customer Services', NULL, N'Rent / DEWA / AC Cheque Inquiry', N'LCS-CHK-001 Rent / DEWA / AC Cheque Inquiry', N'New request type under business review (UAT). Request Group: Leasing. Inquiry regarding rent, DEWA or air-conditioning cheques. Proposed workflow (source text): Leasing CS Queue → Agent → Review → Resolve → Close Required documents: Cheque copy if applicable.', 3, 1, N'["Customer/Tenant","Property/Unit","Cheque type/details"]', 1),
(29, N'BRK-COM-001', N'Customer Service', N'CS', N'Broker Commission Inquiry', N'BRK-COM-001 Broker Commission Inquiry', N'New request type under business review (UAT). Request Group: Broker Inquiries. Broker inquiry related to commission status/payment. Proposed workflow (source text): CS Queue →Cs Agent  → Admin sales →Review → Resolve → Close Required documents: Supporting commission documents if needed.', 3, 1, N'["Broker","Project/Unit","Deal reference"]', 2),
(30, N'BRK-CHK-001', N'Customer Service', N'CS', N'Broker Cheque Collection', N'BRK-CHK-001 Broker Cheque Collection', N'New request type under business review (UAT). Request Group: Broker Inquiries. Broker request/inquiry related to cheque collection. Proposed workflow (source text): CS Queue →Cs Agent  → Admin sales → Confirm collection → Resolve → Close Required documents: Supporting document if needed.', 3, 1, N'["Broker","Deal reference","Cheque details"]', 1),
(31, N'SAL-INQ-001', N'Customer Service', N'CS', N'Property Sales Inquiry', N'SAL-INQ-001 Property Sales Inquiry', N'New request type under business review (UAT). Request Group: Sales. Customer inquiry about available properties or sales. Proposed workflow (source text): CS / Call Center → Sales Handoff → Follow-up → Resolve / Close Required documents: None.', 3, 1, N'["Customer","Contact details","Interested project/unit if known"]', 1),
(32, N'LEG-INQ-001', N'Customer Service', N'CS', N'Legal Notice / Permit / Approval Inquiry', N'LEG-INQ-001 Legal Notice / Permit / Approval Inquiry', N'New request type under business review (UAT). Request Group: Legal. Customer inquiry involving legal notice, permit or approval. Proposed workflow (source text): CS Intake → Legal Handoff → Legal Review/Response → CS Resolve → Close Required documents: Legal notice / relevant documents.', 2, 1, N'["Customer","Project/Unit","Legal request details"]', 2),
(33, N'REC-HR-001', N'Customer Service', N'CS', N'HR Inquiry', N'REC-HR-001 HR Inquiry', N'New request type under business review (UAT). Request Group: Reception. Reception inquiry that should be routed to HR. Proposed workflow (source text): Reception / CS → HR Handoff → Acknowledge/Resolve → Close Required documents: None.', 4, 1, N'["Requester details","Description"]', 1),
(34, N'REC-MKT-001', N'Customer Service', N'CS', N'Marketing Inquiry', N'REC-MKT-001 Marketing Inquiry', N'New request type under business review (UAT). Request Group: Reception. Reception inquiry that should be routed to Marketing. Proposed workflow (source text): Reception / CS → Marketing Handoff → Acknowledge/Resolve → Close Required documents: None.', 4, 1, N'["Requester details","Description"]', 1),
(35, N'REC-OTH-001', N'Customer Service', N'CS', N'Other Reception Inquiry', N'REC-OTH-001 Other Reception Inquiry', N'New request type under business review (UAT). Request Group: Reception. Reception inquiry that does not match a predefined type. Proposed workflow (source text): Reception / CS → Route to Responsible Department → Resolve → Close Required documents: None.', 3, 1, N'["Requester details","Description"]', 1);

INSERT INTO @Steps ([RequestCode], [Sequence], [Name], [Kind], [IsOptional]) VALUES
(N'CS-GEN-001', 1, N'Ticket Created', 1, 0),
(N'CS-GEN-001', 2, N'CS Queue', 2, 0),
(N'CS-GEN-001', 3, N'Agent', 2, 0),
(N'CS-GEN-001', 4, N'Resolve', 8, 0),
(N'CS-GEN-001', 5, N'Close', 9, 0),
(N'CS-GEN-002', 1, N'Ticket Created', 1, 0),
(N'CS-GEN-002', 2, N'CS Queue', 2, 0),
(N'CS-GEN-002', 3, N'Agent', 2, 0),
(N'CS-GEN-002', 4, N'Resolve', 8, 0),
(N'CS-GEN-002', 5, N'Close', 9, 0),
(N'CS-GEN-003', 1, N'Ticket Created', 1, 0),
(N'CS-GEN-003', 2, N'CS Queue', 2, 0),
(N'CS-GEN-003', 3, N'Agent', 2, 0),
(N'CS-GEN-003', 4, N'Obtain update if needed', 5, 1),
(N'CS-GEN-003', 5, N'Resolve', 8, 0),
(N'CS-GEN-003', 6, N'Close', 9, 0),
(N'CS-CMP-001', 1, N'Ticket Created', 1, 0),
(N'CS-CMP-001', 2, N'CS Queue', 2, 0),
(N'CS-CMP-001', 3, N'Agent', 2, 0),
(N'CS-CMP-001', 4, N'Escalate if needed', 5, 1),
(N'CS-CMP-001', 5, N'Resolve', 8, 0),
(N'CS-CMP-001', 6, N'Close', 9, 0),
(N'CS-CMP-002', 1, N'Ticket Created', 1, 0),
(N'CS-CMP-002', 2, N'CS Queue', 2, 0),
(N'CS-CMP-002', 3, N'Agent', 2, 0),
(N'CS-CMP-002', 4, N'Record / route', 5, 0),
(N'CS-CMP-002', 5, N'Resolve', 8, 0),
(N'CS-CMP-002', 6, N'Close', 9, 0),
(N'REG-NOC-001', 1, N'Ticket Created', 1, 0),
(N'REG-NOC-001', 2, N'CS Queue', 2, 0),
(N'REG-NOC-001', 3, N'CS Agent', 2, 0),
(N'REG-NOC-001', 4, N'Transfer to Accounting', 2, 0),
(N'REG-NOC-001', 5, N'Return to CS Agent', 2, 0),
(N'REG-NOC-001', 6, N'Resolve', 8, 0),
(N'REG-NOC-001', 7, N'Close', 9, 0),
(N'REG-NOC-002', 1, N'Ticket Created', 1, 0),
(N'REG-NOC-002', 2, N'CS Queue', 2, 0),
(N'REG-NOC-002', 3, N'CS Agent', 2, 0),
(N'REG-NOC-002', 4, N'Transfer to Accounting', 2, 0),
(N'REG-NOC-002', 5, N'Return to CS Agent', 2, 0),
(N'REG-NOC-002', 6, N'Resolve', 8, 0),
(N'REG-NOC-002', 7, N'Close', 9, 0),
(N'REG-NOC-003', 1, N'Ticket Created', 1, 0),
(N'REG-NOC-003', 2, N'CS Queue', 2, 0),
(N'REG-NOC-003', 3, N'CS Agent', 2, 0),
(N'REG-NOC-003', 4, N'Transfer to Accounting', 2, 0),
(N'REG-NOC-003', 5, N'Return to CS Agent', 2, 0),
(N'REG-NOC-003', 6, N'Resolve', 8, 0),
(N'REG-NOC-003', 7, N'Close', 9, 0),
(N'REG-CON-001', 1, N'Ticket Created', 1, 0),
(N'REG-CON-001', 2, N'Registration Queue', 2, 0),
(N'REG-CON-001', 3, N'Agent', 2, 0),
(N'REG-CON-001', 4, N'Review', 5, 0),
(N'REG-CON-001', 5, N'Resolve', 8, 0),
(N'REG-CON-001', 6, N'Close', 9, 0),
(N'REG-DLD-001', 1, N'Ticket Created', 1, 0),
(N'REG-DLD-001', 2, N'Registration Queue', 2, 0),
(N'REG-DLD-001', 3, N'Agent', 2, 0),
(N'REG-DLD-001', 4, N'Review DLD / registration status', 5, 0),
(N'REG-DLD-001', 5, N'Resolve', 8, 0),
(N'REG-DLD-001', 6, N'Close', 9, 0),
(N'COL-PAY-001', 1, N'Ticket Created', 1, 0),
(N'COL-PAY-001', 2, N'Collections Queue', 2, 0),
(N'COL-PAY-001', 3, N'Agent', 2, 0),
(N'COL-PAY-001', 4, N'Review account', 5, 0),
(N'COL-PAY-001', 5, N'Resolve', 8, 0),
(N'COL-PAY-001', 6, N'Close', 9, 0),
(N'COL-PAY-002', 1, N'Ticket Created', 1, 0),
(N'COL-PAY-002', 2, N'Collections Queue', 2, 0),
(N'COL-PAY-002', 3, N'Agent', 2, 0),
(N'COL-PAY-002', 4, N'Follow-up', 5, 0),
(N'COL-PAY-002', 5, N'Escalate if needed', 5, 1),
(N'COL-PAY-002', 6, N'Resolve', 8, 0),
(N'COL-PAY-002', 7, N'Close', 9, 0),
(N'COL-PAY-003', 1, N'Ticket Created', 1, 0),
(N'COL-PAY-003', 2, N'Collections Queue', 2, 0),
(N'COL-PAY-003', 3, N'Agent', 2, 0),
(N'COL-PAY-003', 4, N'Confirm cheque / collection', 5, 0),
(N'COL-PAY-003', 5, N'Resolve', 8, 0),
(N'COL-PAY-003', 6, N'Close', 9, 0),
(N'COL-PAY-004', 1, N'Ticket Created', 1, 0),
(N'COL-PAY-004', 2, N'Collections Queue', 2, 0),
(N'COL-PAY-004', 3, N'Agent', 2, 0),
(N'COL-PAY-004', 4, N'Review', 5, 0),
(N'COL-PAY-004', 5, N'Resolve', 8, 0),
(N'COL-PAY-004', 6, N'Close', 9, 0),
(N'HO-NOC-001', 1, N'Ticket Created', 1, 0),
(N'HO-NOC-001', 2, N'CS Queue', 2, 0),
(N'HO-NOC-001', 3, N'Agent', 2, 0),
(N'HO-NOC-001', 4, N'Transfer to Accounting', 2, 0),
(N'HO-NOC-001', 5, N'Return to CS Agent', 2, 0),
(N'HO-NOC-001', 6, N'Transfer to Handover Agent', 2, 0),
(N'HO-NOC-001', 7, N'Resolve', 8, 0),
(N'HO-NOC-001', 8, N'Close', 9, 0),
(N'HO-HND-001', 1, N'Ticket Created', 1, 0),
(N'HO-HND-001', 2, N'Handover Queue', 2, 0),
(N'HO-HND-001', 3, N'Agent', 2, 0),
(N'HO-HND-001', 4, N'Coordinate', 5, 0),
(N'HO-HND-001', 5, N'Resolve', 8, 0),
(N'HO-HND-001', 6, N'Close', 9, 0),
(N'HO-HND-002', 1, N'Ticket Created', 1, 0),
(N'HO-HND-002', 2, N'Handover Queue', 2, 0),
(N'HO-HND-002', 3, N'Agent', 2, 0),
(N'HO-HND-002', 4, N'Confirm available slot', 5, 0),
(N'HO-HND-002', 5, N'Resolve', 8, 0),
(N'HO-HND-002', 6, N'Close', 9, 0),
(N'HO-HND-003', 1, N'Ticket Created', 1, 0),
(N'HO-HND-003', 2, N'Handover Queue', 2, 0),
(N'HO-HND-003', 3, N'Agent', 2, 0),
(N'HO-HND-003', 4, N'Coordinate requirements', 5, 0),
(N'HO-HND-003', 5, N'Resolve', 8, 0),
(N'HO-HND-003', 6, N'Close', 9, 0),
(N'HO-HND-004', 1, N'Ticket Created', 1, 0),
(N'HO-HND-004', 2, N'Handover Queue', 2, 0),
(N'HO-HND-004', 3, N'Facilities Management if needed', 11, 1),
(N'HO-HND-004', 4, N'Follow-up', 5, 0),
(N'HO-HND-004', 5, N'Resolve', 8, 0),
(N'HO-HND-004', 6, N'Close', 9, 0),
(N'FM-MNT-001', 1, N'Ticket Created', 1, 0),
(N'FM-MNT-001', 2, N'FM Queue', 2, 0),
(N'FM-MNT-001', 3, N'Assign Agent / Technician', 2, 0),
(N'FM-MNT-001', 4, N'In Progress', 5, 0),
(N'FM-MNT-001', 5, N'Resolve', 8, 0),
(N'FM-MNT-001', 6, N'Close', 9, 0),
(N'FM-UTL-001', 1, N'Ticket Created', 1, 0),
(N'FM-UTL-001', 2, N'FM Queue', 2, 0),
(N'FM-UTL-001', 3, N'Agent', 2, 0),
(N'FM-UTL-001', 4, N'Review / coordinate', 5, 0),
(N'FM-UTL-001', 5, N'Resolve', 8, 0),
(N'FM-UTL-001', 6, N'Close', 9, 0),
(N'FM-COM-001', 1, N'Ticket Created', 1, 0),
(N'FM-COM-001', 2, N'FM Queue', 2, 0),
(N'FM-COM-001', 3, N'Agent / Technician', 2, 0),
(N'FM-COM-001', 4, N'In Progress', 5, 0),
(N'FM-COM-001', 5, N'Resolve', 8, 0),
(N'FM-COM-001', 6, N'Close', 9, 0),
(N'FM-SVC-001', 1, N'Ticket Created', 1, 0),
(N'FM-SVC-001', 2, N'FM / Responsible Finance Queue', 2, 0),
(N'FM-SVC-001', 3, N'Agent', 2, 0),
(N'FM-SVC-001', 4, N'Review', 5, 0),
(N'FM-SVC-001', 5, N'Resolve', 8, 0),
(N'FM-SVC-001', 6, N'Close', 9, 0),
(N'LCS-TEN-001', 1, N'Ticket Created', 1, 0),
(N'LCS-TEN-001', 2, N'Leasing CS Queue', 2, 0),
(N'LCS-TEN-001', 3, N'Agent', 2, 0),
(N'LCS-TEN-001', 4, N'Process / review', 5, 0),
(N'LCS-TEN-001', 5, N'Resolve', 8, 0),
(N'LCS-TEN-001', 6, N'Close', 9, 0),
(N'LCS-EJR-001', 1, N'Ticket Created', 1, 0),
(N'LCS-EJR-001', 2, N'Leasing CS Queue', 2, 0),
(N'LCS-EJR-001', 3, N'Agent', 2, 0),
(N'LCS-EJR-001', 4, N'Process / review', 5, 0),
(N'LCS-EJR-001', 5, N'Resolve', 8, 0),
(N'LCS-EJR-001', 6, N'Close', 9, 0),
(N'LCS-BKG-001', 1, N'Ticket Created', 1, 0),
(N'LCS-BKG-001', 2, N'Leasing CS Queue', 2, 0),
(N'LCS-BKG-001', 3, N'Agent', 2, 0),
(N'LCS-BKG-001', 4, N'Confirm booking / details', 5, 0),
(N'LCS-BKG-001', 5, N'Resolve', 8, 0),
(N'LCS-BKG-001', 6, N'Close', 9, 0),
(N'LCS-MOV-001', 1, N'Ticket Created', 1, 0),
(N'LCS-MOV-001', 2, N'Leasing CS Queue', 2, 0),
(N'LCS-MOV-001', 3, N'Agent', 2, 0),
(N'LCS-MOV-001', 4, N'Coordinate', 5, 0),
(N'LCS-MOV-001', 5, N'Resolve', 8, 0),
(N'LCS-MOV-001', 6, N'Close', 9, 0),
(N'LCS-CHK-001', 1, N'Ticket Created', 1, 0),
(N'LCS-CHK-001', 2, N'Leasing CS Queue', 2, 0),
(N'LCS-CHK-001', 3, N'Agent', 2, 0),
(N'LCS-CHK-001', 4, N'Review', 5, 0),
(N'LCS-CHK-001', 5, N'Resolve', 8, 0),
(N'LCS-CHK-001', 6, N'Close', 9, 0),
(N'BRK-COM-001', 1, N'Ticket Created', 1, 0),
(N'BRK-COM-001', 2, N'CS Queue', 2, 0),
(N'BRK-COM-001', 3, N'CS Agent', 2, 0),
(N'BRK-COM-001', 4, N'Transfer to Admin Sales', 2, 0),
(N'BRK-COM-001', 5, N'Review', 5, 0),
(N'BRK-COM-001', 6, N'Resolve', 8, 0),
(N'BRK-COM-001', 7, N'Close', 9, 0),
(N'BRK-CHK-001', 1, N'Ticket Created', 1, 0),
(N'BRK-CHK-001', 2, N'CS Queue', 2, 0),
(N'BRK-CHK-001', 3, N'CS Agent', 2, 0),
(N'BRK-CHK-001', 4, N'Transfer to Admin Sales', 2, 0),
(N'BRK-CHK-001', 5, N'Confirm collection', 5, 0),
(N'BRK-CHK-001', 6, N'Resolve', 8, 0),
(N'BRK-CHK-001', 7, N'Close', 9, 0),
(N'SAL-INQ-001', 1, N'Ticket Created', 1, 0),
(N'SAL-INQ-001', 2, N'CS / Call Center Queue', 2, 0),
(N'SAL-INQ-001', 3, N'Sales Handoff', 2, 0),
(N'SAL-INQ-001', 4, N'Follow-up', 5, 0),
(N'SAL-INQ-001', 5, N'Resolve', 8, 0),
(N'SAL-INQ-001', 6, N'Close', 9, 0),
(N'LEG-INQ-001', 1, N'Ticket Created', 1, 0),
(N'LEG-INQ-001', 2, N'CS Intake', 2, 0),
(N'LEG-INQ-001', 3, N'Legal Handoff', 2, 0),
(N'LEG-INQ-001', 4, N'Legal Review / Response', 5, 0),
(N'LEG-INQ-001', 5, N'CS Resolve', 8, 0),
(N'LEG-INQ-001', 6, N'Close', 9, 0),
(N'REC-HR-001', 1, N'Ticket Created', 1, 0),
(N'REC-HR-001', 2, N'Reception / CS', 2, 0),
(N'REC-HR-001', 3, N'HR Handoff', 2, 0),
(N'REC-HR-001', 4, N'Acknowledge / Resolve', 8, 0),
(N'REC-HR-001', 5, N'Close', 9, 0),
(N'REC-MKT-001', 1, N'Ticket Created', 1, 0),
(N'REC-MKT-001', 2, N'Reception / CS', 2, 0),
(N'REC-MKT-001', 3, N'Marketing Handoff', 2, 0),
(N'REC-MKT-001', 4, N'Acknowledge / Resolve', 8, 0),
(N'REC-MKT-001', 5, N'Close', 9, 0),
(N'REC-OTH-001', 1, N'Ticket Created', 1, 0),
(N'REC-OTH-001', 2, N'Reception / CS', 2, 0),
(N'REC-OTH-001', 3, N'Route to Responsible Department', 2, 0),
(N'REC-OTH-001', 4, N'Resolve', 8, 0),
(N'REC-OTH-001', 5, N'Close', 9, 0);

INSERT INTO @Destinations ([RequestCode], [Ordinal], [DepartmentName], [DepartmentCode]) VALUES
(N'REG-NOC-001', 1, N'Accounting', N'ACC'),
(N'REG-NOC-001', 2, N'Customer Service', N'CS'),
(N'REG-NOC-002', 1, N'Accounting', N'ACC'),
(N'REG-NOC-002', 2, N'Customer Service', N'CS'),
(N'REG-NOC-003', 1, N'Accounting', N'ACC'),
(N'REG-NOC-003', 2, N'Customer Service', N'CS'),
(N'HO-NOC-001', 1, N'Accounting', N'ACC'),
(N'HO-NOC-001', 2, N'Customer Service', N'CS'),
(N'HO-NOC-001', 3, N'Handover', N'HO'),
(N'HO-HND-004', 1, N'Facilities Management', N'FM'),
(N'BRK-COM-001', 1, N'Admin Sales', NULL),
(N'BRK-CHK-001', 1, N'Admin Sales', NULL),
(N'SAL-INQ-001', 1, N'Sales', NULL),
(N'LEG-INQ-001', 1, N'Legal', NULL),
(N'REC-HR-001', 1, N'HR', NULL),
(N'REC-MKT-001', 1, N'Marketing', NULL);
-- </generated-data>

IF (SELECT COUNT(*) FROM @Rows) <> 35
BEGIN
    THROW 50003, N'Expected exactly 35 workbook rows.', 1;
END;

-------------------------------------------------------------------------------
-- 2. Plan: resolve departments and decide each row's outcome. Read-only.
-------------------------------------------------------------------------------

DECLARE @Plan TABLE (
    [RequestCode]            nvarchar(24)  NOT NULL PRIMARY KEY,
    [DepartmentId]           int           NULL,
    [ExistingWorkflowId]     int           NULL,
    [ImportedRequestTypeId]  int           NULL,
    [SameNameRequestTypeId]  int           NULL,
    [VersionCodeInUse]       bit           NOT NULL DEFAULT 0,
    [UnresolvedDestinations] nvarchar(400) NULL,
    [Outcome]                nvarchar(40)  NULL,
    [RequestTypeId]          int           NULL);

-- Departments: by code first (unique IX_Departments_Code), then by exact
-- name (unique IX_Departments_Name). Never created.
INSERT INTO @Plan ([RequestCode], [DepartmentId])
SELECT r.[RequestCode],
       COALESCE(
           (SELECT d.[DepartmentId] FROM [Departments] d WHERE r.[OwnerDepartmentCode] IS NOT NULL AND d.[Code] = r.[OwnerDepartmentCode]),
           (SELECT d.[DepartmentId] FROM [Departments] d WHERE d.[Name] = r.[OwnerDepartmentName]))
FROM @Rows r;

UPDATE p
SET [UnresolvedDestinations] = STUFF((
        SELECT N', ' + x.[DepartmentName]
        FROM @Destinations x
        WHERE x.[RequestCode] = p.[RequestCode]
          AND NOT EXISTS (SELECT 1 FROM [Departments] d WHERE x.[DepartmentCode] IS NOT NULL AND d.[Code] = x.[DepartmentCode])
          AND NOT EXISTS (SELECT 1 FROM [Departments] d WHERE d.[Name] = x.[DepartmentName])
        ORDER BY x.[Ordinal]
        FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'')
FROM @Plan p;

UPDATE p SET [ExistingWorkflowId] = w.[WorkflowId]
FROM @Plan p INNER JOIN [Workflows] w ON w.[Code] = p.[RequestCode];

UPDATE p SET [ImportedRequestTypeId] = (
        SELECT MIN(rt.[RequestTypeId]) FROM [RequestTypes] rt
        WHERE rt.[WorkflowId] = p.[ExistingWorkflowId] AND rt.[DepartmentId] = p.[DepartmentId])
FROM @Plan p
WHERE p.[ExistingWorkflowId] IS NOT NULL;

UPDATE p SET [VersionCodeInUse] = 1
FROM @Plan p
WHERE EXISTS (SELECT 1 FROM [WorkflowTemplates] t WHERE t.[Code] = p.[RequestCode]);

UPDATE p SET [SameNameRequestTypeId] = rt.[RequestTypeId]
FROM @Plan p
INNER JOIN @Rows r ON r.[RequestCode] = p.[RequestCode]
INNER JOIN [RequestTypes] rt ON rt.[DepartmentId] = p.[DepartmentId] AND rt.[Name] = r.[Name];

UPDATE @Plan
SET [Outcome] = CASE
        WHEN [DepartmentId] IS NULL                                            THEN N'SkippedOwningDepartmentMissing'
        WHEN [ExistingWorkflowId] IS NOT NULL AND [ImportedRequestTypeId] IS NOT NULL THEN N'AlreadyImported'
        WHEN [ExistingWorkflowId] IS NOT NULL OR [VersionCodeInUse] = 1        THEN N'SkippedCodeInUse'
        WHEN [SameNameRequestTypeId] IS NOT NULL                               THEN N'SkippedExistingRequestType'
        WHEN [UnresolvedDestinations] IS NULL                                  THEN N'Created'
        ELSE N'CreatedWithDraftWorkflow'
    END,
    [RequestTypeId] = COALESCE([ImportedRequestTypeId], [SameNameRequestTypeId]);

PRINT '=== PLAN ===';
SELECT p.[RequestCode], p.[Outcome], [UnresolvedDestinations] = ISNULL(p.[UnresolvedDestinations], N'')
FROM @Plan p INNER JOIN @Rows r ON r.[RequestCode] = p.[RequestCode]
ORDER BY r.[Ordinal];

-------------------------------------------------------------------------------
-- 3. Apply — additive inserts only, in one transaction.
-------------------------------------------------------------------------------

BEGIN TRANSACTION;

DECLARE @Now datetime2 = SYSUTCDATETIME();
DECLARE @Code nvarchar(24), @DepartmentId int, @Name nvarchar(100), @WorkflowName nvarchar(100),
        @WorkflowDescription nvarchar(500), @PriorityId tinyint, @AllowReopen bit, @RequiredFieldsJson nvarchar(2000),
        @ResolutionDays int, @Publish bit, @WorkflowId int, @TemplateId int, @RequestTypeId int;

DECLARE import_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT r.[RequestCode], p.[DepartmentId], r.[Name], r.[WorkflowName], r.[WorkflowDescription], r.[DefaultPriorityId],
           r.[AllowReopen], r.[RequiredFieldsJson], r.[ResolutionDays],
           CASE WHEN p.[Outcome] = N'Created' THEN 1 ELSE 0 END
    FROM @Rows r
    INNER JOIN @Plan p ON p.[RequestCode] = r.[RequestCode]
    WHERE p.[Outcome] IN (N'Created', N'CreatedWithDraftWorkflow')
    ORDER BY r.[Ordinal];

OPEN import_cursor;
FETCH NEXT FROM import_cursor INTO @Code, @DepartmentId, @Name, @WorkflowName, @WorkflowDescription, @PriorityId,
    @AllowReopen, @RequiredFieldsJson, @ResolutionDays, @Publish;

WHILE @@FETCH_STATUS = 0
BEGIN
    INSERT INTO [Workflows] ([Code], [Name], [Description], [IsActive], [CreatedAtUtc])
    VALUES (@Code, @WorkflowName, @WorkflowDescription, 1, @Now);
    SET @WorkflowId = CAST(SCOPE_IDENTITY() AS int);

    -- Version 1 carries the workflow code (the Workflow Designer's convention).
    -- Status 2 = Published, 1 = Draft. No approval / pending step exists in
    -- any translation, so the capability flags stay 0 exactly as the
    -- designer's publish would derive them.
    INSERT INTO [WorkflowTemplates]
        ([Code], [Name], [Description], [AllowsPendingCustomer], [AllowsPendingInternal], [RequiresApproval], [IsActive],
         [WorkflowId], [VersionNumber], [Status], [CreatedAtUtc], [CreatedByEmployeeId], [PublishedAtUtc], [PublishedByEmployeeId])
    VALUES
        (@Code, @WorkflowName, @WorkflowDescription, 0, 0, 0, 1,
         @WorkflowId, 1, CASE WHEN @Publish = 1 THEN 2 ELSE 1 END, @Now, NULL, CASE WHEN @Publish = 1 THEN @Now END, NULL);
    SET @TemplateId = CAST(SCOPE_IDENTITY() AS int);

    INSERT INTO [WorkflowTemplateSteps] ([WorkflowTemplateId], [Sequence], [Name], [Kind], [IsOptional], [ApprovalType])
    SELECT @TemplateId, s.[Sequence], s.[Name], s.[Kind], s.[IsOptional], NULL
    FROM @Steps s
    WHERE s.[RequestCode] = @Code
    ORDER BY s.[Sequence];

    INSERT INTO [RequestTypes]
        ([DepartmentId], [Name], [WorkflowId], [DefaultPriorityId], [AllowAgentPriorityChange], [AllowPendingCustomer],
         [AllowPendingInternal], [AllowReopen], [RequiredFieldsJson], [IsActive])
    VALUES
        (@DepartmentId, @Name, @WorkflowId, @PriorityId, 0, 0, 0, @AllowReopen, @RequiredFieldsJson, 0);
    SET @RequestTypeId = CAST(SCOPE_IDENTITY() AS int);

    -- Trigger 1 = TicketCreated, Unit 3 = Days, ClockBasis 2 = BusinessHours.
    -- First Response is deliberately NULL (see the report: one unit per row).
    IF @ResolutionDays IS NOT NULL
    BEGIN
        INSERT INTO [RequestTypeSlaPolicies]
            ([RequestTypeId], [PriorityId], [Trigger], [Unit], [FirstResponseTargetValue], [FirstResponseMaximumValue],
             [ResolutionTargetValue], [ResolutionMaximumValue], [IsImmediate], [ClockBasis], [PausesOnPendingCustomer],
             [PausesOnPendingInternal], [WarningThresholdPercent], [IsActive])
        VALUES
            (@RequestTypeId, @PriorityId, 1, 3, NULL, NULL, @ResolutionDays, NULL, 0, 2, NULL, NULL, NULL, 1);
    END;

    UPDATE @Plan SET [RequestTypeId] = @RequestTypeId WHERE [RequestCode] = @Code;

    FETCH NEXT FROM import_cursor INTO @Code, @DepartmentId, @Name, @WorkflowName, @WorkflowDescription, @PriorityId,
        @AllowReopen, @RequiredFieldsJson, @ResolutionDays, @Publish;
END;

CLOSE import_cursor;
DEALLOCATE import_cursor;

-------------------------------------------------------------------------------
-- 4. Verification — 35/35 reconciliation, then hard checks.
-------------------------------------------------------------------------------

PRINT '=== RECONCILIATION (35 workbook rows) ===';

SELECT
    r.[RequestCode],
    p.[Outcome],
    [Department]       = ISNULL(d.[Code] + N' — ' + d.[Name], N'(missing: ' + r.[OwnerDepartmentName] + N')'),
    [RequestType]      = ISNULL(rt.[Name], r.[Name]),
    [RequestTypeId]    = p.[RequestTypeId],
    [Active]           = CASE WHEN rt.[RequestTypeId] IS NULL THEN N'—' WHEN rt.[IsActive] = 1 THEN N'Active' ELSE N'Inactive' END,
    [Workflow]         = ISNULL(w.[Code], N'—'),
    [WorkflowVersion]  = CASE t.[Status] WHEN 1 THEN N'v1 Draft' WHEN 2 THEN N'v1 Published' WHEN 3 THEN N'v1 Historical' ELSE N'—' END,
    [Steps]            = (SELECT COUNT(*) FROM [WorkflowTemplateSteps] s WHERE s.[WorkflowTemplateId] = t.[WorkflowTemplateId]),
    [ResolutionSla]    = ISNULL((SELECT TOP (1) CONCAT(sla.[ResolutionTargetValue], N' business day(s)')
                                 FROM [RequestTypeSlaPolicies] sla
                                 WHERE sla.[RequestTypeId] = rt.[RequestTypeId] AND w.[Code] = r.[RequestCode]), N'—'),
    [UnresolvedDestinations] = ISNULL(p.[UnresolvedDestinations], N'')
FROM @Rows r
INNER JOIN @Plan p ON p.[RequestCode] = r.[RequestCode]
LEFT JOIN [RequestTypes] rt ON rt.[RequestTypeId] = p.[RequestTypeId]
LEFT JOIN [Departments] d ON d.[DepartmentId] = p.[DepartmentId]
LEFT JOIN [Workflows] w ON w.[WorkflowId] = rt.[WorkflowId]
LEFT JOIN [WorkflowTemplates] t ON t.[WorkflowId] = w.[WorkflowId] AND t.[VersionNumber] = 1
ORDER BY r.[Ordinal];

SELECT [Outcome], [Rows] = COUNT(*) FROM @Plan GROUP BY [Outcome] ORDER BY [Outcome];

-- Exactly one request type per imported code, in its owning department.
IF EXISTS (
    SELECT w.[Code]
    FROM [Workflows] w
    INNER JOIN @Rows r ON r.[RequestCode] = w.[Code]
    INNER JOIN [RequestTypes] rt ON rt.[WorkflowId] = w.[WorkflowId]
    GROUP BY w.[Code]
    HAVING COUNT(*) > 1)
BEGIN
    THROW 50010, N'A Request Code is linked to more than one request type — inspect before continuing.', 1;
END;

-- Every created / already-imported row is present.
IF EXISTS (
    SELECT 1 FROM @Plan p
    WHERE p.[Outcome] IN (N'Created', N'CreatedWithDraftWorkflow', N'AlreadyImported')
      AND NOT EXISTS (SELECT 1 FROM [RequestTypes] rt WHERE rt.[RequestTypeId] = p.[RequestTypeId]))
BEGIN
    THROW 50011, N'An imported row has no request type — inspect before continuing.', 1;
END;

IF @CommitChanges = 1
BEGIN
    COMMIT TRANSACTION;
    PRINT 'Committed.';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION;
    PRINT 'DRY RUN — rolled back; nothing was changed.';
END;
GO
