/*
  probe_crm_pact_payment.sql - READ-ONLY probe of the CRM database (CRMDB). Run in SSMS and send back every result grid.
  Purpose: (1) find what the CRM "Customer Details -> Payment -> Pact Payment" screen reads, (2) prove which PACT project code CRM project 94 (VOLGA Tower) carries,
           (3) show what CRM holds for lead 307092 (TP140-407) and customer 437381 (BEN ISRAELI, unit 3302).
  No INSERT/UPDATE/DELETE/DDL. SELECT * is used on purpose so no column name is guessed. Results contain customer data: send them only through a private channel.
*/
SET NOCOUNT ON;

/* 1. Any stored procedure / view / function whose NAME or BODY mentions PACT or payment (the Pact Payment tab's data source is one of these, or an EF query in the CRM code) */
SELECT '1_objects' AS section, o.type_desc, SCHEMA_NAME(o.schema_id) AS [schema], o.name
FROM sys.objects o
WHERE o.type IN ('P','V','FN','IF','TF')
  AND (o.name LIKE '%pact%' OR o.name LIKE '%payment%' OR o.name LIKE '%edsm%' OR o.name LIKE '%receiv%')
ORDER BY o.type_desc, o.name;

SELECT '1b_bodies_mentioning_pact' AS section, SCHEMA_NAME(o.schema_id) AS [schema], o.name, o.type_desc
FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id
WHERE m.definition LIKE '%pact%' OR m.definition LIKE '%p4tenantStat%' OR m.definition LIKE '%p32tenantStat%' OR m.definition LIKE '%payment-summary%'
ORDER BY o.name;

/* 2. Linked servers (a PACT linked server would show here) */
SELECT '2_linked_servers' AS section, name, product, provider, data_source FROM sys.servers WHERE is_linked = 1;

/* 3. Columns of the lead / project / unit / payment tables (names + types only) */
SELECT '3_columns' AS section, t.name AS [table], c.name AS [column], ty.name AS data_type, c.max_length, c.is_nullable
FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE t.name IN (N'tblLead', N'tblLeads', N'tblLeadCustomers', N'tblUnits', N'tblProjects', N'tblCustomers', N'tblPayment')
ORDER BY t.name, c.column_id;

/* 4. CRM project 94: the project row and a sample of its units / leads (look for the PACT project code, e.g. TP125) */
SELECT '4_project_94' AS section, p.* FROM tblProjects p WHERE p.ID = 94;
SELECT TOP 20 '4b_units_and_leads_of_project_94' AS section, u.ID AS UnitId, u.Number AS UnitNumber, l.*
FROM tblUnits u
LEFT JOIN tblLeadCustomers lc ON lc.UnitID = u.ID
LEFT JOIN tblLeads l ON l.ID = lc.LeadID
WHERE u.ProjectID = 94
ORDER BY CASE WHEN u.Number = N'3302' THEN 0 ELSE 1 END, u.ID;

/* 5. Lead 307092 (TP140-407) and customer 437381 (unit 3302): the full lead row, its customers, its unit and project */
SELECT '5_lead_307092' AS section, l.* FROM tblLeads l WHERE l.ID = 307092;
SELECT '5b_lead_customers_307092' AS section, lc.*, c.* FROM tblLeadCustomers lc LEFT JOIN tblCustomers c ON c.ID = lc.CustomerID WHERE lc.LeadID = 307092;
SELECT '5c_customer_437381_units' AS section, lc.LeadID, lc.UnitID, lc.CustomerType, u.Number, u.ProjectID, p.Name AS ProjectName
FROM tblLeadCustomers lc LEFT JOIN tblUnits u ON u.ID = lc.UnitID LEFT JOIN tblProjects p ON p.ID = u.ProjectID WHERE lc.CustomerID = 437381;

/* 6. The legacy EDSM exclusion query derives the PACT unit code in CRM as TRIM(ProjectCode)+'-'+TRIM(UnitNumber) on tblLead. Show it for the two units. */
SELECT TOP 20 '6_tblLead_unit_codes' AS section, l.ID, TRIM(l.ProjectCode) AS ProjectCode, TRIM(l.UnitNumber) AS UnitNumber, TRIM(TRIM(l.ProjectCode) + '-' + TRIM(l.UnitNumber)) AS UnitCode, l.Status
FROM tblLead l WHERE l.ID = 307092 OR (TRIM(l.UnitNumber) = N'3302' AND l.ID IN (SELECT lc.LeadID FROM tblLeadCustomers lc WHERE lc.CustomerID = 437381));

/* 7. If tblPayment feeds the Pact Payment tab: its rows for lead 307092 */
SELECT TOP 50 '7_tblPayment_lead_307092' AS section, p.* FROM tblPayment p WHERE p.LeadID = 307092 ORDER BY p.DueDate;
