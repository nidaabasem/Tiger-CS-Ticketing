-- =====================================================================
-- Genesys tickets filed under Finance (TG-FIN-...) on UAT — INVESTIGATION
-- Read-only. Run against the UAT TigerCsTicketing database.
--
-- Why this script exists: the TigerCS code has NO fallback department.
-- A Genesys ticket's department is decided, in this order and only from
-- the request the Genesys Data Action sent:
--   1. departmentId   (explicit, website chat picker)
--   2. departmentCode (explicit, IVR / chat menu / Messenger "department" attribute)
--   3. the active GenesysQueueMappings row for the queueId the flow sent
-- Nothing else; an unresolved department is answered 422 and no ticket is
-- created. So a TG-FIN ticket from Genesys means one of those three inputs
-- named Finance. The queries below say which.
-- =====================================================================
SET NOCOUNT ON;

DECLARE @FinanceCode nvarchar(10) = N'FIN';
DECLARE @Since datetime2 = DATEADD(day, -30, SYSUTCDATETIME());

-- 0. Departments and their ids (confirms which id is Finance on THIS database;
--    the website chat picker sends a departmentId, and ids differ per environment).
SELECT DepartmentId, Code, Name, IsActive FROM Departments ORDER BY DepartmentId;

-- 1. Every Genesys queue mapping — a mapping that points at Finance routes
--    every call/chat on that queue to Finance. QueueName is display-only;
--    matching is on QueueId.
SELECT m.GenesysQueueMappingId, m.QueueId, m.QueueName, m.DepartmentId, d.Code AS DepartmentCode, d.Name AS DepartmentName,
       m.IsActive, m.CreatedAtUtc, m.UpdatedAtUtc
FROM GenesysQueueMappings m
JOIN Departments d ON d.DepartmentId = m.DepartmentId
ORDER BY d.Code, m.QueueId;

-- 2. Genesys-created tickets now in Finance, with the queue the interaction
--    reported and what that queue is mapped to today.
SELECT t.TicketId, t.TicketNumber, t.CreatedAtUtc, t.RequestSummary,
       od.Code AS OriginatingDept, cd.Code AS CurrentDept, t.TicketStatus,
       i.GenesysConversationId, i.GenesysQueueId, i.GenesysQueueName,
       md.Code AS QueueMappedToToday, m.IsActive AS MappingActive
FROM Tickets t
JOIN Departments cd ON cd.DepartmentId = t.CurrentDepartmentId
JOIN Departments od ON od.DepartmentId = t.OriginatingDepartmentId
JOIN TicketInteractions i ON i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.GenesysConversationId IS NOT NULL
LEFT JOIN GenesysQueueMappings m ON m.QueueId = i.GenesysQueueId
LEFT JOIN Departments md ON md.DepartmentId = m.DepartmentId
WHERE (od.Code = @FinanceCode OR cd.Code = @FinanceCode)
  AND t.CreatedAtUtc >= @Since
ORDER BY t.CreatedAtUtc DESC;

-- 3. The ingestion audit entry for each of those conversations. The deployed
--    build (004dff9) records DepartmentId and QueueId; the build in this
--    release additionally records DepartmentSource=ExplicitDepartmentId |
--    ExplicitDepartmentCode | QueueMapping plus ReceivedDepartmentId /
--    ReceivedDepartmentCode / QueueName — i.e. the exact input that chose
--    the department. For entries written by the old build, read the
--    "Decision" column: a queue mapped to Finance → QueueMapping; otherwise
--    the flow sent an explicit Finance id/code.
SELECT a.OccurredAtUtc, a.EntityId AS ConversationId, a.AfterValue,
       CASE
         WHEN a.AfterValue LIKE '%DepartmentSource=%' THEN 'see DepartmentSource= in AfterValue'
         WHEN m.DepartmentId = fin.DepartmentId THEN 'QueueMapping → Finance (queue ' + ISNULL(i.GenesysQueueId, '?') + ')'
         WHEN i.GenesysQueueId IS NULL THEN 'No queue sent → flow sent explicit departmentId/departmentCode = Finance'
         ELSE 'Queue not mapped to Finance → flow sent explicit departmentId/departmentCode = Finance'
       END AS Decision
FROM AuditEntries a
JOIN TicketInteractions i ON i.GenesysConversationId = a.EntityId AND i.IsOriginatingInteraction = 1
JOIN Tickets t ON t.TicketId = i.TicketId
JOIN Departments fin ON fin.Code = @FinanceCode
LEFT JOIN GenesysQueueMappings m ON m.QueueId = i.GenesysQueueId
WHERE a.Action = 'GenesysInquiryIngested'
  AND a.EntityType = 'GenesysConversation'
  AND t.OriginatingDepartmentId = fin.DepartmentId
  AND a.OccurredAtUtc >= @Since
ORDER BY a.OccurredAtUtc DESC;

-- 4. The specific ticket from the report: subject TH_CS_Leasing_WM.
--    (The subject is what the flow sent in "subject" — if it equals a queue
--    name, the flow maps subject ← queue name.)
SELECT t.TicketId, t.TicketNumber, t.RequestSummary, t.CreatedAtUtc,
       od.Code AS OriginatingDept, cd.Code AS CurrentDept,
       i.GenesysConversationId, i.GenesysQueueId, i.GenesysQueueName
FROM Tickets t
JOIN Departments cd ON cd.DepartmentId = t.CurrentDepartmentId
JOIN Departments od ON od.DepartmentId = t.OriginatingDepartmentId
LEFT JOIN TicketInteractions i ON i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1
WHERE t.RequestSummary LIKE '%TH_CS_Leasing_WM%';

-- 5. Which Genesys queues have produced tickets but have NO active mapping
--    (these are refused 422 today — a configuration gap, not a Finance one).
SELECT i.GenesysQueueId, i.GenesysQueueName, COUNT(*) AS Tickets, MIN(t.CreatedAtUtc) AS FirstSeen, MAX(t.CreatedAtUtc) AS LastSeen
FROM TicketInteractions i
JOIN Tickets t ON t.TicketId = i.TicketId
LEFT JOIN GenesysQueueMappings m ON m.QueueId = i.GenesysQueueId AND m.IsActive = 1
WHERE i.GenesysConversationId IS NOT NULL AND i.GenesysQueueId IS NOT NULL AND m.GenesysQueueMappingId IS NULL
GROUP BY i.GenesysQueueId, i.GenesysQueueName
ORDER BY LastSeen DESC;
