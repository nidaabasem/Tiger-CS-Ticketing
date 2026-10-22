/*
    Cancels the human-handoff rows an earlier version of the Genesys integration wrote only because a ticket arrived without a
    request type ("Awaiting classification: ..."), e.g. ticket TG-LCS-20261021-0002. Those rows made Ticketing show
    "Waiting for agent" and "System - asked for a human agent" for conversations the AI was still handling.

    Safe to run more than once. It touches ONLY rows that are
      - still outstanding (ResolvedAtUtc IS NULL) and still WaitingForAgent (no agent ever took them),
      - raised with Trigger = RoutingDecision (4) and a reason starting "Awaiting classification",
    and it does not touch the ticket (status, owner, department, SLA), the interaction, or any customer-requested / AI-escalated handoff.
    Newly classified tickets stand these rows down automatically; this script is for the ones that are still Unclassified.

    Review the preview (first SELECT) before running the UPDATE.   Roll back by restoring from the pre-run backup/export of
    TicketAgentHandoffs - there is no schema change.
*/
SET XACT_ABORT ON;

SELECT h.TicketAgentHandoffId, t.TicketNumber, h.RequestedAtUtc, h.RequestReason
FROM dbo.TicketAgentHandoffs h
JOIN dbo.Tickets t ON t.TicketId = h.TicketId
WHERE h.ResolvedAtUtc IS NULL
  AND h.Status = 2            -- WaitingForAgent
  AND h.Trigger = 4           -- RoutingDecision
  AND h.RequestReason LIKE N'Awaiting classification%';

BEGIN TRANSACTION;

DECLARE @now DATETIME2 = SYSUTCDATETIME();
DECLARE @cancelled TABLE (TicketAgentHandoffId BIGINT, TicketId BIGINT);

UPDATE h
SET    h.Status = 6,                                   -- Cancelled
       h.ResolvedAtUtc = @now,
       h.ResolutionNote = N'Stood down: a missing request type is not a request for a human (cleanup of legacy "Awaiting classification" work).'
OUTPUT inserted.TicketAgentHandoffId, inserted.TicketId INTO @cancelled
FROM   dbo.TicketAgentHandoffs h
WHERE  h.ResolvedAtUtc IS NULL
  AND  h.Status = 2
  AND  h.Trigger = 4
  AND  h.RequestReason LIKE N'Awaiting classification%';

-- The ticket's typed event log: one HandoffCancelled (14) per row, so Ticket Details tells the same story.
INSERT INTO dbo.TicketWorkflowEvents (TicketId, EventType, OccurredAtUtc, ActorEmployeeId, TicketApprovalId, Note, CorrelationId)
SELECT c.TicketId, 14, @now, NULL, NULL,
       N'Legacy "Awaiting classification" work stood down: a missing request type is not a request for a human.', NEWID()
FROM   @cancelled c;

SELECT COUNT(*) AS CancelledRows FROM @cancelled;

COMMIT TRANSACTION;
