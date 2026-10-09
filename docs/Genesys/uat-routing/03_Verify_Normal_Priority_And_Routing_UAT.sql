/*
    03_Verify_Normal_Priority_And_Routing_UAT.sql
    =============================================
    READ-ONLY. Run against the UAT database before testing Genesys default
    priority and request-type routing. It changes nothing.

    Every section prints PASS/FAIL-style rows; fix any FAIL before UAT:
      1. Priorities + the priority "Normal" resolves to (name 'Normal', else the Medium tier).
      2. The SLA policy of that priority, and the business calendar.
      3. Request types usable for routing: active, active department, Published workflow.
      4. Per request type: assignment rule, SLA row on the Normal tier (informational).
      5. Genesys queue mappings and channels.
*/
SET NOCOUNT ON;

PRINT '--- 1. Priorities and the resolved Normal priority';
SELECT PriorityId, Name FROM Priorities ORDER BY PriorityId;

DECLARE @NormalId tinyint =
    COALESCE((SELECT PriorityId FROM Priorities WHERE Name = N'Normal'),
             (SELECT PriorityId FROM Priorities WHERE Name = N'Medium'));
SELECT CASE WHEN @NormalId IS NULL THEN 'FAIL: no Normal/Medium priority row - new Genesys tickets would get NO priority and NO SLA'
            ELSE 'PASS: Normal resolves to PriorityId ' + CAST(@NormalId AS varchar(3)) END AS NormalPriority;

PRINT '--- 2. SLA policy for the Normal priority, and the calendar';
SELECT CASE WHEN EXISTS (SELECT 1 FROM SlaPolicies WHERE PriorityId = @NormalId)
            THEN 'PASS: SlaPolicies row exists' ELSE 'FAIL: no SlaPolicies row for the Normal priority - ticket creation would fail' END AS NormalSlaPolicy;
SELECT * FROM SlaPolicies WHERE PriorityId = @NormalId;
SELECT CASE WHEN EXISTS (SELECT 1 FROM BusinessCalendars)
            THEN 'PASS: a business calendar exists' ELSE 'FAIL: no BusinessCalendars row - business-hours tiers cannot be computed' END AS Calendar;

PRINT '--- 3. Request types usable for routing (every row below must be PASS to route)';
SELECT rt.RequestTypeId, rt.Name, d.Code AS DepartmentCode, d.IsActive AS DepartmentActive, rt.IsActive AS RequestTypeActive,
       (SELECT COUNT(*) FROM WorkflowTemplates t WHERE t.WorkflowId = rt.WorkflowId AND t.Status = 2) AS PublishedVersions,
       CASE WHEN rt.IsActive = 1 AND d.IsActive = 1
                 AND EXISTS (SELECT 1 FROM WorkflowTemplates t WHERE t.WorkflowId = rt.WorkflowId AND t.Status = 2)
            THEN 'PASS' ELSE 'FAIL: inactive, inactive department, or no Published workflow' END AS Routable,
       -- a name shared by two active request types in different departments cannot be sent by name
       CASE WHEN (SELECT COUNT(*) FROM RequestTypes x WHERE x.IsActive = 1 AND x.Name = rt.Name) > 1
            THEN 'WARN: ambiguous by name - Genesys must send requestTypeId' ELSE '' END AS NameAmbiguity
FROM RequestTypes rt JOIN Departments d ON d.DepartmentId = rt.DepartmentId
ORDER BY d.Code, rt.Name;

PRINT '--- 4. Assignment rules and Normal-tier request-type SLA rows (no rule = department queue; no SLA row = per-priority policy)';
SELECT rt.RequestTypeId, rt.Name,
       r.Mode AS AssignmentMode, r.IsActive AS RuleActive,
       sp.RequestTypeSlaPolicyId, sp.IsActive AS SlaRowActive, sp.Trigger, sp.ClockBasis
FROM RequestTypes rt
LEFT JOIN RequestTypeAssignmentRules r ON r.RequestTypeId = rt.RequestTypeId
LEFT JOIN RequestTypeSlaPolicies sp ON sp.RequestTypeId = rt.RequestTypeId AND sp.PriorityId = @NormalId
WHERE rt.IsActive = 1
ORDER BY rt.Name;

PRINT '--- 5. Queue mappings and channels';
SELECT q.QueueId, q.QueueName, d.Code AS DepartmentCode, d.IsActive FROM GenesysQueueMappings q JOIN Departments d ON d.DepartmentId = q.DepartmentId;
SELECT Code, IsActive FROM Channels ORDER BY Code;
