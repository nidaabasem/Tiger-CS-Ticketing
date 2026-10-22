-- READ-ONLY. Verifies, on the real database, that a Genesys Leasing request can be routed and auto-assigned.
-- Run each block; nothing here writes. (Result sets only - no INSERT/UPDATE/DELETE.)

-- 1. Is there an ACTIVE Leasing department? (the import creates "Leasing Customer Services", code LCS)
SELECT DepartmentId, Name, Code, IsActive
FROM Departments
WHERE Code = N'LCS' OR Name LIKE N'%Leasing%';

-- 2. Leasing request types: active, owned by the Leasing department, usable (Published workflow, 2 = Published).
--    A type literally named 'Leasing' is what { "requestType": "Leasing" } resolves; the imported types are
--    Tenancy Contract, Ejari, Booking, Move-In / Move-Out, Rent / DEWA / AC Cheque Inquiry.
SELECT rt.RequestTypeId, rt.Name, rt.IsActive, d.Code AS DepartmentCode, d.IsActive AS DepartmentActive,
       CASE WHEN EXISTS (SELECT 1 FROM WorkflowTemplates w WHERE w.WorkflowId = rt.WorkflowId AND w.Status = 2)
            THEN 1 ELSE 0 END AS HasPublishedWorkflow
FROM RequestTypes rt
JOIN Departments d ON d.DepartmentId = rt.DepartmentId
WHERE d.Code = N'LCS' OR rt.Name LIKE N'%Leasing%'
ORDER BY rt.Name;

-- 3. Same name in more than one department would be refused as ambiguous - should return no rows for 'Leasing'.
SELECT Name, COUNT(*) AS ActiveCount
FROM RequestTypes WHERE IsActive = 1 AND Name = N'Leasing'
GROUP BY Name HAVING COUNT(*) > 1;

-- 4. Automatic assignment: one rule per request type. No row = tickets wait in the department queue.
SELECT rt.RequestTypeId, rt.Name, r.Mode, r.PrimaryEmployeeId, r.TeamName, r.IsActive AS RuleActive,
       (SELECT COUNT(*) FROM RequestTypeAssignmentRuleMembers m WHERE m.RequestTypeAssignmentRuleId = r.RequestTypeAssignmentRuleId) AS Members
FROM RequestTypes rt
JOIN Departments d ON d.DepartmentId = rt.DepartmentId
LEFT JOIN RequestTypeAssignmentRules r ON r.RequestTypeId = rt.RequestTypeId
WHERE d.Code = N'LCS' OR rt.Name LIKE N'%Leasing%';

-- 5. The department must have members for a rule to pick (assignment requires membership of the responsible department).
SELECT a.EmployeeId, a.IsPrimary FROM UserDepartmentAssignments a
JOIN Departments d ON d.DepartmentId = a.DepartmentId WHERE d.Code = N'LCS';

-- 6. Department workflow settings that can switch automatic assignment off for the department.
SELECT s.* FROM DepartmentWorkflowSettings s JOIN Departments d ON d.DepartmentId = s.DepartmentId WHERE d.Code = N'LCS';

-- 7. After a UAT call: the ticket, its interaction and follow-up flag.
SELECT t.TicketNumber, t.TicketStatus, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.RequestTypeId, t.CurrentOwnerEmployeeId,
       i.GenesysConversationId, i.GenesysServiceRequestId, i.RequiresHumanFollowUp, i.InactivityClosedAtUtc
FROM TicketInteractions i JOIN Tickets t ON t.TicketId = i.TicketId
WHERE i.GenesysConversationId = N'TEST-CONVERSATION-001';
