-- Local harness ONLY: a loopback linked server named [10.10.10.94] so the real refresh procedure's INSERT ... EXEC path runs against a fake PACT database.
IF DB_ID('TigerCsTicketing') IS NULL CREATE DATABASE TigerCsTicketing;
GO
IF DB_ID('PACTRPT') IS NULL CREATE DATABASE PACTRPT;
GO
IF NOT EXISTS (SELECT 1 FROM sys.servers WHERE name = N'10.10.10.94')
BEGIN
  EXEC sp_addlinkedserver @server = N'10.10.10.94', @srvproduct = N'', @provider = N'MSOLEDBSQL', @datasrc = N'localhost';
  EXEC sp_addlinkedsrvlogin @rmtsrvname = N'10.10.10.94', @useself = N'FALSE', @locallogin = NULL, @rmtuser = N'sa', @rmtpassword = N'$(LOCALSQL_PASSWORD)';
  EXEC sp_serveroption N'10.10.10.94', N'rpc out', N'true';
  EXEC sp_serveroption N'10.10.10.94', N'data access', N'true';
END
GO
SELECT name, provider, data_source FROM sys.servers;
