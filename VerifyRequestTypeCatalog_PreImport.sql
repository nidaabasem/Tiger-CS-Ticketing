/*
    VerifyRequestTypeCatalog_PreImport.sql — READ-ONLY (SELECT only).
    Run BEFORE the migrations. Save the output: the post-import script's
    "existing NOC" results must match it exactly.
*/
SET NOCOUNT ON;

-- Latest applied migration: must be 20260921133507_AddAgentHandoffTriggerAndConcurrency,
-- and neither catalog migration may be listed yet.
SELECT TOP (3) MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC;

-- Departments the workbook names: a MISSING one means its rows will be skipped.
SELECT v.Department, CASE WHEN d.DepartmentId IS NULL THEN 'MISSING - rows will be skipped'
                          WHEN d.IsActive = 0 THEN 'exists (inactive)' ELSE 'exists' END AS Status
FROM (VALUES
    (N'Collections'),
    (N'Customer Service'),
    (N'Facilities Management'),
    (N'Handover'),
    (N'Leasing Customer Services'),
    (N'Registration')
) v(Department)
LEFT JOIN Departments d ON d.Name = v.Department
ORDER BY v.Department;

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

-- The business calendar actually configured (reference only — nothing in
-- the import assumes a window, work week or holiday list).
SELECT BusinessCalendarId, Name, TimeZone, BusinessDayStartLocal, BusinessDayEndLocal, IsActive FROM BusinessCalendars;
SELECT w.BusinessCalendarId, w.DayOfWeek, w.IsWorkingDay FROM BusinessCalendarWorkingDays w ORDER BY w.BusinessCalendarId, w.DayOfWeek;
