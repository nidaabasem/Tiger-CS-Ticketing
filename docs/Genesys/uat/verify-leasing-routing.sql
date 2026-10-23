-- READ-ONLY. Verifies, on the real database, that a Genesys Leasing request can be routed and auto-assigned.
-- Run each block; nothing here writes. (Result sets only - no INSERT/UPDATE/DELETE.)
--
-- The dedicated request type is "Leasing Inquiry" in Leasing Customer Services (LCS), default priority Normal
-- (= the Medium tier, PriorityId 3), with a Published workflow. Genesys sends it as { "requestType": "Leasing Inquiry" }
-- (a bare string is read as the name) or by requestTypeId. It is created from Administration > Request Types, never by SQL.
-- Conventions: a Priority/PriorityLevel of 3 is Medium/"Normal"; WorkflowTemplates.Status 2 = Published;
-- an employee is active when Employees.DeactivatedAtUtc IS NULL.

-- 1. Is there an ACTIVE Leasing department? (the import creates "Leasing Customer Services", code LCS)
--    Expect exactly one row with IsActive = 1.
SELECT DepartmentId, Name, Code, IsActive
FROM Departments
WHERE Code = N'LCS' OR Name LIKE N'%Leasing%';

-- 2. Existing LCS request types, with the workflow each one uses and whether that workflow has a Published version.
--    The imported types are Tenancy Contract, Ejari, Booking, Move-In / Move-Out, Rent / DEWA / AC Cheque Inquiry;
--    "Leasing Inquiry" appears here once an administrator has created it. HasPublishedWorkflow must be 1 for any type Genesys uses.
SELECT rt.RequestTypeId, rt.Name, rt.IsActive, rt.DefaultPriorityId,
       d.Code AS DepartmentCode, d.IsActive AS DepartmentActive,
       rt.WorkflowId, w.Code AS WorkflowCode, w.Name AS WorkflowName, w.IsActive AS WorkflowActive,
       CASE WHEN EXISTS (SELECT 1 FROM WorkflowTemplates t WHERE t.WorkflowId = rt.WorkflowId AND t.Status = 2)
            THEN 1 ELSE 0 END AS HasPublishedWorkflow
FROM RequestTypes rt
JOIN Departments d ON d.DepartmentId = rt.DepartmentId
LEFT JOIN Workflows w ON w.WorkflowId = rt.WorkflowId
WHERE d.Code = N'LCS' OR rt.Name LIKE N'%Leasing%'
ORDER BY rt.Name;

-- 2b. The target type specifically: "Leasing Inquiry". Zero rows = not created yet; two rows would be a duplicate
--     (Administration refuses a second one with the same name in a department).
--     Expect: IsActive = 1, DepartmentCode = LCS, DefaultPriorityId = 3 (Normal/Medium), HasPublishedWorkflow = 1.
SELECT rt.RequestTypeId, rt.Name, rt.IsActive, d.Code AS DepartmentCode, rt.DefaultPriorityId,
       CASE WHEN EXISTS (SELECT 1 FROM WorkflowTemplates t WHERE t.WorkflowId = rt.WorkflowId AND t.Status = 2)
            THEN 1 ELSE 0 END AS HasPublishedWorkflow
FROM RequestTypes rt
JOIN Departments d ON d.DepartmentId = rt.DepartmentId
WHERE rt.Name = N'Leasing Inquiry';

-- 3. Same active name in more than one department would be refused as ambiguous - must return no rows for 'Leasing Inquiry'.
SELECT Name, COUNT(*) AS ActiveCount
FROM RequestTypes WHERE IsActive = 1 AND Name IN (N'Leasing Inquiry', N'Leasing')
GROUP BY Name HAVING COUNT(*) > 1;

-- 4. Published workflows reachable from the LCS request types (version number and step count).
SELECT rt.Name AS RequestType, w.Code AS WorkflowCode, t.WorkflowTemplateId, t.VersionNumber, t.Status,
       (SELECT COUNT(*) FROM WorkflowTemplateSteps s WHERE s.WorkflowTemplateId = t.WorkflowTemplateId) AS Steps
FROM RequestTypes rt
JOIN Departments d ON d.DepartmentId = rt.DepartmentId
JOIN Workflows w ON w.WorkflowId = rt.WorkflowId
JOIN WorkflowTemplates t ON t.WorkflowId = w.WorkflowId AND t.Status = 2
WHERE d.Code = N'LCS'
ORDER BY rt.Name;

-- 5. Existing assignment rules (one per request type). Mode: 1 = DepartmentQueue, 2 = SpecificEmployee, 3 = Team
--    (see AssignmentMode). No row, or RuleActive = 0, means tickets wait in the LCS department queue.
SELECT rt.RequestTypeId, rt.Name, r.RequestTypeAssignmentRuleId, r.Mode, r.PrimaryEmployeeId, e.DisplayName AS PrimaryEmployee,
       r.TeamName, r.IsActive AS RuleActive,
       (SELECT COUNT(*) FROM RequestTypeAssignmentRuleMembers m WHERE m.RequestTypeAssignmentRuleId = r.RequestTypeAssignmentRuleId) AS Members
FROM RequestTypes rt
JOIN Departments d ON d.DepartmentId = rt.DepartmentId
LEFT JOIN RequestTypeAssignmentRules r ON r.RequestTypeId = rt.RequestTypeId
LEFT JOIN Employees e ON e.EmployeeId = r.PrimaryEmployeeId
WHERE d.Code = N'LCS' OR rt.Name LIKE N'%Leasing%'
ORDER BY rt.Name;

-- 6. ACTIVE LCS department members - the only employees an assignment rule may pick.
SELECT a.EmployeeId, e.DisplayName, a.IsPrimary
FROM UserDepartmentAssignments a
JOIN Departments d ON d.DepartmentId = a.DepartmentId
JOIN Employees e ON e.EmployeeId = a.EmployeeId
WHERE d.Code = N'LCS' AND e.DeactivatedAtUtc IS NULL
ORDER BY e.DisplayName;

-- 7. LCS request types with NO usable assignment rule (none, inactive, or department-queue mode):
--    their tickets will wait in the LCS queue unassigned. Informational - a queue is a valid choice.
SELECT rt.RequestTypeId, rt.Name, rt.IsActive,
       CASE WHEN r.RequestTypeAssignmentRuleId IS NULL THEN N'no rule'
            WHEN r.IsActive = 0 THEN N'rule inactive'
            WHEN r.Mode = 1 THEN N'department-queue rule'
            ELSE N'other' END AS Reason
FROM RequestTypes rt
JOIN Departments d ON d.DepartmentId = rt.DepartmentId
LEFT JOIN RequestTypeAssignmentRules r ON r.RequestTypeId = rt.RequestTypeId
WHERE d.Code = N'LCS' AND rt.IsActive = 1
  AND (r.RequestTypeAssignmentRuleId IS NULL OR r.IsActive = 0 OR r.Mode = 1)
ORDER BY rt.Name;

-- 8. Assignment rules (LCS request types) whose primary assignee or team member is a deactivated employee or NOT an LCS member.
--    At assignment time such a rule silently degrades to the LCS queue (ConfiguredAssigneeNotInDepartment) - fix it in Administration.
SELECT rt.Name AS RequestType, r.RequestTypeAssignmentRuleId, N'primary' AS Role, r.PrimaryEmployeeId AS EmployeeId, e.DisplayName,
       CASE WHEN e.EmployeeId IS NULL THEN N'employee not found'
            WHEN e.DeactivatedAtUtc IS NOT NULL THEN N'employee deactivated'
            ELSE N'not an LCS member' END AS Problem
FROM RequestTypeAssignmentRules r
JOIN RequestTypes rt ON rt.RequestTypeId = r.RequestTypeId
JOIN Departments d ON d.DepartmentId = rt.DepartmentId AND d.Code = N'LCS'
LEFT JOIN Employees e ON e.EmployeeId = r.PrimaryEmployeeId
WHERE r.PrimaryEmployeeId IS NOT NULL
  AND (e.EmployeeId IS NULL OR e.DeactivatedAtUtc IS NOT NULL
       OR NOT EXISTS (SELECT 1 FROM UserDepartmentAssignments a WHERE a.EmployeeId = r.PrimaryEmployeeId AND a.DepartmentId = d.DepartmentId))
UNION ALL
SELECT rt.Name, r.RequestTypeAssignmentRuleId, N'team member', m.EmployeeId, e.DisplayName,
       CASE WHEN e.EmployeeId IS NULL THEN N'employee not found'
            WHEN e.DeactivatedAtUtc IS NOT NULL THEN N'employee deactivated'
            ELSE N'not an LCS member' END
FROM RequestTypeAssignmentRuleMembers m
JOIN RequestTypeAssignmentRules r ON r.RequestTypeAssignmentRuleId = m.RequestTypeAssignmentRuleId
JOIN RequestTypes rt ON rt.RequestTypeId = r.RequestTypeId
JOIN Departments d ON d.DepartmentId = rt.DepartmentId AND d.Code = N'LCS'
LEFT JOIN Employees e ON e.EmployeeId = m.EmployeeId
WHERE e.EmployeeId IS NULL OR e.DeactivatedAtUtc IS NOT NULL
   OR NOT EXISTS (SELECT 1 FROM UserDepartmentAssignments a WHERE a.EmployeeId = m.EmployeeId AND a.DepartmentId = d.DepartmentId);

-- 9. Department workflow settings that can switch automatic assignment off for the department (AllowAssignment = 0 => queue).
SELECT s.* FROM DepartmentWorkflowSettings s JOIN Departments d ON d.DepartmentId = s.DepartmentId WHERE d.Code = N'LCS';

-- 10. After a UAT call: the ticket, its interaction and follow-up flag. Expect a separate ticket per serviceRequestId,
--     CurrentDepartmentId = LCS, OriginatingDepartmentId = the Genesys arrival department, RequestTypeId = Leasing Inquiry,
--     PriorityId = 3, RequiresHumanFollowUp = 1, InactivityClosedAtUtc NULL, and (with a rule) CurrentOwnerEmployeeId set.
SELECT t.TicketNumber, t.TicketStatus, t.PriorityId, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.RequestTypeId, t.CurrentOwnerEmployeeId,
       i.GenesysConversationId, i.GenesysServiceRequestId, i.RequiresHumanFollowUp, i.InactivityClosedAtUtc
FROM TicketInteractions i JOIN Tickets t ON t.TicketId = i.TicketId
WHERE i.GenesysConversationId = N'TEST-CONVERSATION-001';

-- 11. Why a ticket was (not) auto-assigned: the audit entry written by the assignment automation.
--     AfterValue "AssignedEmployeeId=..." = assigned; "DepartmentQueue;...Reason=..." = left in the queue and why.
SELECT TOP (20) a.EntityId AS TicketId, a.Action, a.AfterValue, a.OccurredAtUtc
FROM AuditEntries a
WHERE a.Action = N'AutoAssign' AND a.EntityType = N'Ticket'
ORDER BY a.OccurredAtUtc DESC;
