/*
  ROLLBACK of the V2 deployment. Order matters:
    1. In the application configuration set CollectionsSource:PactReceivables:ProcedureSuffix back to "" (empty) and restart/redeploy the API.
       The originals were never changed, so the application is then exactly as before the deployment. Review records read through V2 stay
       readable but become stale; refresh the review data once.
    2. Only then run this script. It drops ONLY the V2 procedures, and only if they exist.
  Dropping is optional: the V2 procedures are inert while the suffix is empty.
*/
IF OBJECT_ID(N'dbo.p4AccountReceivablesV2', N'P') IS NOT NULL DROP PROCEDURE dbo.p4AccountReceivablesV2;
IF OBJECT_ID(N'dbo.p32AccountReceivablesV2', N'P') IS NOT NULL DROP PROCEDURE dbo.p32AccountReceivablesV2;
SELECT N'p4' AS Proc4, OBJECT_ID(N'dbo.p4AccountReceivablesV2', N'P') AS StillThere4, OBJECT_ID(N'dbo.p32AccountReceivablesV2', N'P') AS StillThere32;
