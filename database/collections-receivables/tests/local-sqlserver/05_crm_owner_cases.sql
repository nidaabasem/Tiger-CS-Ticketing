-- Synthetic CRM owners for the EDGE units of 03_edge_cases.sql (NOT real customers). Run on TigerCsTicketing AFTER V010 and AFTER 03 + one refresh.
--   EDGE-01 TP101-9001 one customer, same phone as PACT      -> Single, CRM name wins, no conflict
--   EDGE-02 TP101-9002 two qualifying customers              -> Ambiguous (flag 128), nobody picked
--   EDGE-05 TP102-9051 one customer, a DIFFERENT phone       -> ContactSourceConflict (flag 256), CRM alone
--   EDGE-14 TP104-9141 PACT has no phone / e-mail            -> CRM completes the contact (no NoValidContact)
--   TP105-9221         PACT unit only; CRM row of tower 105 with the same key and a second customer on another unit: no mixing
-- Cancelled / former-owner / non-buyer rows never reach this table (the application refuses them before storing).
DECLARE @run uniqueidentifier = NEWID(), @json nvarchar(max) = N'[
 {"unitKey":"TP101-9001","projectCode":"TP101","unitNumber":"9001","leadId":1,"leadStatus":8,"customerId":501,"unitId":9000001,"projectId":1,"fullName":"Crm Edge One","mobile":"0501110001","email":"crm01@example.test","phoneNorm":"+971501110001","emailNorm":"crm01@example.test"},
 {"unitKey":"TP101-9002","projectCode":"TP101","unitNumber":"9002","leadId":2,"leadStatus":8,"customerId":502,"unitId":9000002,"projectId":1,"fullName":"Crm Two A","mobile":"0502220002","email":"a@example.test","phoneNorm":"+971502220002","emailNorm":"a@example.test"},
 {"unitKey":"TP101-9002","projectCode":"TP101","unitNumber":"9002","leadId":3,"leadStatus":4,"customerId":503,"unitId":9000002,"projectId":1,"fullName":"Crm Two B","mobile":"0503330003","email":"b@example.test","phoneNorm":"+971503330003","emailNorm":"b@example.test"},
 {"unitKey":"TP102-9051","projectCode":"TP102","unitNumber":"9051","leadId":4,"leadStatus":8,"customerId":505,"unitId":9000051,"projectId":2,"fullName":"Crm Five","mobile":"0551119999","email":"crm05@example.test","phoneNorm":"+971551119999","emailNorm":"crm05@example.test"},
 {"unitKey":"TP104-9141","projectCode":"TP104","unitNumber":"9141","leadId":5,"leadStatus":8,"customerId":514,"unitId":9000141,"projectId":4,"fullName":"Crm Fourteen","mobile":"0501110014","email":"crm14@example.test","phoneNorm":"+971501110014","emailNorm":"crm14@example.test"}
]';
EXEC dbo.usp_Collections_StoreCrmUnitOwners @RunId = @run, @Json = @json;
EXEC dbo.usp_Collections_PublishCrmUnitOwners @RunId = @run, @ExpectedRows = 5;
GO
