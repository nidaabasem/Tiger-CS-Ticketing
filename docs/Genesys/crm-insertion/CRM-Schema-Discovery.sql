/* =====================================================================
   Tiger CRM - schema discovery for GetUnitDetails  (READ-ONLY)
   - Reads ONLY system catalog views (sys.*). No table data is read.
   - No INSERT/UPDATE/DELETE/DDL, no temp tables, no dynamic SQL.
   - Run on the CRM database in SSMS; "Results to Grid"; save every result set
     (right-click > Save Results As...) or copy all grids and send them back.
   ===================================================================== */
SET NOCOUNT ON;

/* 0. Where am I */
SELECT '0_database' AS section, DB_NAME() AS database_name,
       CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) AS sql_version;

/* 1. Columns + types of the tables the existing code already uses */
SELECT '1_core_columns' AS section, SCHEMA_NAME(t.schema_id) AS [schema], t.name AS [table],
       c.column_id, c.name AS [column], ty.name AS data_type,
       c.max_length AS max_length_bytes, c.precision, c.scale,
       c.is_nullable, c.is_identity, c.is_computed,
       CAST(ep.value AS nvarchar(400)) AS ms_description
FROM sys.tables t
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types ty  ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.extended_properties ep
       ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = N'MS_Description'
WHERE t.name IN (N'tblCustomers', N'tblLeadCustomers', N'tblLeads', N'tblUnits', N'tblProjects', N'tblAttachments')
ORDER BY t.name, c.column_id;

/* 2. Every foreign key into or out of those tables (the relationships) */
SELECT '2_foreign_keys' AS section, fk.name AS fk_name,
       SCHEMA_NAME(pt.schema_id) AS child_schema,  pt.name AS child_table,  pc.name AS child_column,
       SCHEMA_NAME(rt.schema_id) AS parent_schema, rt.name AS parent_table, rc.name AS parent_column
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables  pt ON pt.object_id = fk.parent_object_id
JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.tables  rt ON rt.object_id = fk.referenced_object_id
JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
WHERE pt.name IN (N'tblCustomers', N'tblLeadCustomers', N'tblLeads', N'tblUnits', N'tblProjects')
   OR rt.name IN (N'tblCustomers', N'tblLeadCustomers', N'tblLeads', N'tblUnits', N'tblProjects')
ORDER BY rt.name, pt.name, fk.name;

/* 3. Columns of every OTHER table directly linked (by FK) to leads/units/projects/lead-customers */
SELECT '3_related_table_columns' AS section, SCHEMA_NAME(t.schema_id) AS [schema], t.name AS [table],
       c.column_id, c.name AS [column], ty.name AS data_type,
       c.max_length AS max_length_bytes, c.precision, c.scale, c.is_nullable,
       CAST(ep.value AS nvarchar(400)) AS ms_description
FROM sys.tables t
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types ty  ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.extended_properties ep
       ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = N'MS_Description'
WHERE t.name NOT IN (N'tblCustomers', N'tblLeadCustomers', N'tblLeads', N'tblUnits', N'tblProjects', N'tblAttachments')
  AND ( t.object_id IN (SELECT f.parent_object_id
                        FROM sys.foreign_keys f JOIN sys.tables r ON r.object_id = f.referenced_object_id
                        WHERE r.name IN (N'tblLeads', N'tblUnits', N'tblProjects', N'tblLeadCustomers'))
     OR t.object_id IN (SELECT f.referenced_object_id
                        FROM sys.foreign_keys f JOIN sys.tables p ON p.object_id = f.parent_object_id
                        WHERE p.name IN (N'tblLeads', N'tblUnits', N'tblProjects', N'tblLeadCustomers')) )
ORDER BY t.name, c.column_id;

/* 4. Columns ANYWHERE whose name suggests the missing facts */
SELECT '4_candidate_columns' AS section, SCHEMA_NAME(t.schema_id) AS [schema], t.name AS [table],
       c.name AS [column], ty.name AS data_type, c.precision, c.scale, c.is_nullable,
       MIN(k.kw) AS matched_keyword, CAST(MAX(CAST(ep.value AS nvarchar(400))) AS nvarchar(400)) AS ms_description
FROM sys.tables t
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types ty  ON ty.user_type_id = c.user_type_id
JOIN (VALUES (N'%price%'), (N'%amount%'), (N'%cost%'), (N'%fee%'), (N'%regist%'), (N'%currency%'),
             (N'%handover%'), (N'%handing%'), (N'%deliver%'), (N'%complet%'), (N'%progress%'), (N'%percent%'),
             (N'%bedroom%'), (N'%bed%'), (N'%tower%'), (N'%building%'), (N'%park%'), (N'%area%'),
             (N'%amenit%'), (N'%address%'), (N'%location%'), (N'%description%'), (N'%discount%'),
             (N'%sold%'), (N'%contract%'), (N'%booking%'), (N'%reserv%'), (N'%unitprice%'), (N'%net%')) AS k(kw)
  ON c.name LIKE k.kw
LEFT JOIN sys.extended_properties ep
       ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = N'MS_Description'
WHERE t.is_ms_shipped = 0
GROUP BY t.schema_id, t.name, c.name, ty.name, c.precision, c.scale, c.is_nullable
ORDER BY t.name, c.name;

/* 5. Tables / views whose NAME suggests parking, towers, amenities, sales, prices, progress, lookups */
SELECT '5_candidate_objects' AS section, o.type_desc, SCHEMA_NAME(o.schema_id) AS [schema], o.name AS object_name
FROM sys.objects o
JOIN (VALUES (N'%park%'), (N'%tower%'), (N'%building%'), (N'%amenit%'), (N'%payment%'), (N'%contract%'),
             (N'%booking%'), (N'%reserv%'), (N'%sale%'), (N'%price%'), (N'%fee%'), (N'%currency%'),
             (N'%handover%'), (N'%construct%'), (N'%progress%'), (N'%milestone%'), (N'%phase%'),
             (N'%lookup%'), (N'%enum%'), (N'%status%'), (N'%type%')) AS k(kw)
  ON o.name LIKE k.kw
WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'V')
GROUP BY o.type_desc, o.schema_id, o.name
ORDER BY o.type_desc, o.name;
