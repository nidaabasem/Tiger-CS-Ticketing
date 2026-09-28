/*
    VerifyRequestTypeCatalog_PostImport.sql — READ-ONLY (SELECT only).
    Run AFTER the import. Every query marked "MUST BE EMPTY" must return no rows.
*/
SET NOCOUNT ON;

-- Both catalog migrations applied.
SELECT MigrationId FROM __EFMigrationsHistory
WHERE MigrationId IN (N'20260928085727_AddRequestTypeCatalogImport', N'20260928102230_AddConfiguredRuntimeEnforcement');

-- The 35 workbook rows and what exists for each.
DECLARE @Catalog TABLE (Code nvarchar(24) PRIMARY KEY, Department nvarchar(100), Name nvarchar(100));
INSERT INTO @Catalog VALUES
    (N'CS-GEN-001', N'Customer Service', N'General Inquiry'),
    (N'CS-GEN-002', N'Customer Service', N'Office Hours / Contact Information'),
    (N'CS-GEN-003', N'Customer Service', N'Construction Update'),
    (N'CS-CMP-001', N'Customer Service', N'Complaint'),
    (N'CS-CMP-002', N'Customer Service', N'Feedback / Suggestion'),
    (N'REG-NOC-001', N'Customer Service', N'NOC for Resale'),
    (N'REG-NOC-002', N'Customer Service', N'NOC for Golden Visa'),
    (N'REG-NOC-003', N'Customer Service', N'NOC for Mortgage'),
    (N'REG-CON-001', N'Registration', N'SPA / Contract Inquiry'),
    (N'REG-DLD-001', N'Registration', N'Ownership Transfer / Title Deed Inquiry'),
    (N'COL-PAY-001', N'Collections', N'Payment / Outstanding Balance Inquiry'),
    (N'COL-PAY-002', N'Collections', N'Returned Cheque'),
    (N'COL-PAY-003', N'Collections', N'Cheque Collection'),
    (N'COL-PAY-004', N'Collections', N'Payment Cheque Inquiry'),
    (N'HO-NOC-001', N'Customer Service', N'NOC for Handover'),
    (N'HO-HND-001', N'Handover', N'Coordinate Handover'),
    (N'HO-HND-002', N'Handover', N'Schedule Handover Appointment'),
    (N'HO-HND-003', N'Handover', N'Move-In / Move-Out'),
    (N'HO-HND-004', N'Handover', N'Handover Maintenance Follow-up'),
    (N'FM-MNT-001', N'Facilities Management', N'Repair / Maintenance Request'),
    (N'FM-UTL-001', N'Facilities Management', N'Utilities Inquiry'),
    (N'FM-COM-001', N'Facilities Management', N'Common Area Maintenance'),
    (N'FM-SVC-001', N'Facilities Management', N'Service Charge Inquiry'),
    (N'LCS-TEN-001', N'Leasing Customer Services', N'Tenancy Contract'),
    (N'LCS-EJR-001', N'Leasing Customer Services', N'Ejari'),
    (N'LCS-BKG-001', N'Leasing Customer Services', N'Booking'),
    (N'LCS-MOV-001', N'Leasing Customer Services', N'Move-In / Move-Out'),
    (N'LCS-CHK-001', N'Leasing Customer Services', N'Rent / DEWA / AC Cheque Inquiry'),
    (N'BRK-COM-001', N'Customer Service', N'Broker Commission Inquiry'),
    (N'BRK-CHK-001', N'Customer Service', N'Broker Cheque Collection'),
    (N'SAL-INQ-001', N'Customer Service', N'Property Sales Inquiry'),
    (N'LEG-INQ-001', N'Customer Service', N'Legal Notice / Permit / Approval Inquiry'),
    (N'REC-HR-001', N'Customer Service', N'HR Inquiry'),
    (N'REC-MKT-001', N'Customer Service', N'Marketing Inquiry'),
    (N'REC-OTH-001', N'Customer Service', N'Other Reception Inquiry');

SELECT c.Code, c.Department, c.Name,
       CASE WHEN rt.RequestTypeId IS NOT NULL THEN 'IMPORTED'
            WHEN ex.RequestTypeId IS NOT NULL THEN 'EXISTING (unchanged)'
            ELSE 'SKIPPED' END AS Outcome,
       COALESCE(rt.RequestTypeId, ex.RequestTypeId) AS RequestTypeId,
       rt.IsActive, rt.ConfigurationEnforced,
       (SELECT COUNT(*) FROM RequestTypeCatalogDecisions x WHERE x.RequestTypeId = rt.RequestTypeId AND x.ResolvedAtUtc IS NULL) AS OpenDecisions
FROM @Catalog c
LEFT JOIN RequestTypes rt ON rt.Code = c.Code
LEFT JOIN Departments d ON d.Name = c.Department
LEFT JOIN RequestTypes ex ON rt.RequestTypeId IS NULL AND ex.DepartmentId = d.DepartmentId AND ex.Name = c.Name
ORDER BY Outcome, c.Code;

-- Totals: Imported / Existing / Skipped (must add up to 35).
SELECT SUM(CASE WHEN rt.RequestTypeId IS NOT NULL THEN 1 ELSE 0 END) AS Imported,
       SUM(CASE WHEN rt.RequestTypeId IS NULL AND ex.RequestTypeId IS NOT NULL THEN 1 ELSE 0 END) AS Existing,
       SUM(CASE WHEN rt.RequestTypeId IS NULL AND ex.RequestTypeId IS NULL THEN 1 ELSE 0 END) AS Skipped
FROM @Catalog c
LEFT JOIN RequestTypes rt ON rt.Code = c.Code
LEFT JOIN Departments d ON d.Name = c.Department
LEFT JOIN RequestTypes ex ON rt.RequestTypeId IS NULL AND ex.DepartmentId = d.DepartmentId AND ex.Name = c.Name;

-- MUST BE EMPTY: an imported type that is active, enforced, has an active or
-- published workflow, or any approval requirement (including Reopen Approval).
SELECT rt.Code, rt.IsActive, rt.ConfigurationEnforced, w.IsActive AS WorkflowActive,
       (SELECT COUNT(*) FROM WorkflowTemplates t WHERE t.WorkflowId = rt.WorkflowId AND t.Status <> 1) AS NonDraftVersions,
       (SELECT COUNT(*) FROM RequestTypeApprovalRequirements a WHERE a.RequestTypeId = rt.RequestTypeId) AS ApprovalRequirements
FROM RequestTypes rt
JOIN Workflows w ON w.WorkflowId = rt.WorkflowId
WHERE rt.Code IS NOT NULL
  AND (rt.IsActive = 1 OR rt.ConfigurationEnforced = 1 OR w.IsActive = 1
       OR EXISTS (SELECT 1 FROM WorkflowTemplates t WHERE t.WorkflowId = rt.WorkflowId AND t.Status <> 1)
       OR EXISTS (SELECT 1 FROM RequestTypeApprovalRequirements a WHERE a.RequestTypeId = rt.RequestTypeId));

-- MUST BE EMPTY: any request type anywhere with enforcement on, or an
-- existing (pre-catalog) type that received a catalog code.
SELECT RequestTypeId, Name, Code, ConfigurationEnforced FROM RequestTypes
WHERE ConfigurationEnforced = 1 OR (Code IS NOT NULL AND Code NOT IN (SELECT Code FROM @Catalog))
   OR (Code IS NOT NULL AND WorkflowId NOT IN (SELECT WorkflowId FROM Workflows WHERE Code = N'RT-' + RequestTypes.Code));

-- MUST BE EMPTY: a ticket tracking a workflow step (nothing is enforced).
SELECT TicketId FROM Tickets WHERE CurrentWorkflowStepId IS NOT NULL;

-- Open business decisions per area (what blocks activation later).
SELECT x.Area, COUNT(*) AS OpenDecisions, COUNT(DISTINCT x.RequestTypeId) AS RequestTypes
FROM RequestTypeCatalogDecisions x WHERE x.ResolvedAtUtc IS NULL GROUP BY x.Area ORDER BY x.Area;

-- Existing NOC request types (Customer Service) — pre-existing columns only,
-- so the SAME query runs before and after; the results must be identical.
DECLARE @Noc TABLE (Name nvarchar(100) PRIMARY KEY);
INSERT INTO @Noc VALUES (N'NOC for Resale'), (N'NOC for Golden Visa'), (N'NOC for Mortgage'), (N'NOC for Handover');

SELECT rt.RequestTypeId, rt.Name, rt.DepartmentId, rt.WorkflowId, rt.DefaultPriorityId,
       rt.AllowAgentPriorityChange, rt.AllowPendingCustomer, rt.AllowPendingInternal, rt.AllowReopen,
       rt.RequiredFieldsJson, rt.IsActive
FROM RequestTypes rt
JOIN Departments d ON d.DepartmentId = rt.DepartmentId AND d.Name = N'Customer Service'
JOIN @Noc n ON n.Name = rt.Name
ORDER BY rt.RequestTypeId;

SELECT p.RequestTypeSlaPolicyId, p.RequestTypeId, p.PriorityId, p.[Trigger], p.Unit, p.FirstResponseTargetValue, p.FirstResponseMaximumValue,
       p.ResolutionTargetValue, p.ResolutionMaximumValue, p.IsImmediate, p.ClockBasis, p.IsActive
FROM RequestTypeSlaPolicies p
JOIN RequestTypes rt ON rt.RequestTypeId = p.RequestTypeId
JOIN Departments d ON d.DepartmentId = rt.DepartmentId AND d.Name = N'Customer Service'
JOIN @Noc n ON n.Name = rt.Name
ORDER BY p.RequestTypeSlaPolicyId;

SELECT a.RequestTypeApprovalRequirementId, a.RequestTypeId, a.ApprovalType, a.TargetKind, a.TargetDepartmentId, a.TargetRoleName,
       a.TargetEmployeeId, a.BlocksWorkUntilApproved, a.IsActive
FROM RequestTypeApprovalRequirements a
JOIN RequestTypes rt ON rt.RequestTypeId = a.RequestTypeId
JOIN Departments d ON d.DepartmentId = rt.DepartmentId AND d.Name = N'Customer Service'
JOIN @Noc n ON n.Name = rt.Name
ORDER BY a.RequestTypeApprovalRequirementId;

-- Row counts outside the catalog (must not change): request types, workflows,
-- versions, SLA rows and approval requirements that existed before.
SELECT (SELECT COUNT(*) FROM RequestTypes) AS RequestTypes, (SELECT COUNT(*) FROM Workflows) AS Workflows,
       (SELECT COUNT(*) FROM WorkflowTemplates) AS WorkflowVersions, (SELECT COUNT(*) FROM RequestTypeSlaPolicies) AS SlaRows,
       (SELECT COUNT(*) FROM RequestTypeApprovalRequirements) AS ApprovalRequirements;

-- Imported rows = RequestTypes delta; the imported types' own workflows,
-- versions, SLA rows (no approval rows) account for the other deltas.
SELECT COUNT(*) AS ImportedRequestTypes,
       (SELECT COUNT(*) FROM Workflows w WHERE w.Code LIKE N'RT-%') AS ImportedWorkflows,
       (SELECT COUNT(*) FROM RequestTypeSlaPolicies p JOIN RequestTypes r ON r.RequestTypeId = p.RequestTypeId WHERE r.Code IS NOT NULL) AS ImportedSlaRows
FROM RequestTypes WHERE Code IS NOT NULL;
