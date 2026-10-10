-- Cases for the day-based Receivables page / Campaigns table (synthetic, NOT PACT data). Run after 03_edge_cases.sql, then refresh the snapshot.
-- Dates assume TODAY (Dubai) = 2026-10-10:  overdue = before it, Due = on it, future = after it (never listed).
-- Every tenant is named VER-nn.
USE PACTRPT;
GO
DELETE dbo.FakeEdgeRows WHERE TenantID LIKE 'VER-%';
INSERT dbo.FakeEdgeRows (CompanyId, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, DueDate, Amount, Status)
VALUES
-- VER-01: one customer, TWO apartments. 101: overdue 60 + overdue 60 + due today 100 (+ a FUTURE 500 that must never count) = 220; 102: overdue 300.
(4, '', 'TP201-101', 'VER-01', N'Ver One', '+971507770001', 'v01@example.test', 8000101, 'V1-A', '20260805', 60,  'Installment'),
(4, '', 'TP201-101', 'VER-01', N'Ver One', '+971507770001', 'v01@example.test', 8000101, 'V1-B', '20260901', 60,  'Installment'),
(4, '', 'TP201-101', 'VER-01', N'Ver One', '+971507770001', 'v01@example.test', 8000101, 'V1-C', '20261010', 100, 'Installment'),
(4, '', 'TP201-101', 'VER-01', N'Ver One', '+971507770001', 'v01@example.test', 8000101, 'V1-F', '20261105', 500, 'Installment'),
(4, '', 'TP201-102', 'VER-01', N'Ver One', '+971507770001', 'v01@example.test', 8000102, 'V1-D', '20260915', 300, 'Installment'),
-- VER-02: apartment 513* is cancelled (900); the same customer's apartment 514 (400) stays.
(4, '', 'TP201-513*', 'VER-02', N'Ver Two', '+971507770002', 'v02@example.test', 8000513, 'V2-A', '20260901', 900, 'Installment'),
(4, '', 'TP201-514',  'VER-02', N'Ver Two', '+971507770002', 'v02@example.test', 8000514, 'V2-B', '20260901', 400, 'Installment'),
-- VER-03: two small instalments, total 120 -> shown with Minimum Total 100.  VER-04: 50 + 50 = 100 -> hidden (not greater).  VER-05: only a future instalment -> never listed.
(4, '', 'TP201-103', 'VER-03', N'Ver Three', '+971507770003', 'v03@example.test', 8000103, 'V3-A', '20260901', 70, 'Installment'),
(4, '', 'TP201-103', 'VER-03', N'Ver Three', '+971507770003', 'v03@example.test', 8000103, 'V3-B', '20260910', 50, 'Installment'),
(4, '', 'TP201-104', 'VER-04', N'Ver Four',  '+971507770004', 'v04@example.test', 8000104, 'V4-A', '20260901', 50, 'Installment'),
(4, '', 'TP201-104', 'VER-04', N'Ver Four',  '+971507770004', 'v04@example.test', 8000104, 'V4-B', '20260910', 50, 'Installment'),
(4, '', 'TP201-105', 'VER-05', N'Ver Five',  '+971507770005', 'v05@example.test', 8000105, 'V5-A', '20261201', 800, 'Installment'),
-- VER-06: a September instalment only (month filter), VER-07: due today only (Due, not Overdue)
(4, '', 'TP201-106', 'VER-06', N'Ver Six',   '+971507770006', 'v06@example.test', 8000106, 'V6-A', '20260920', 250, 'Installment'),
(4, '', 'TP201-107', 'VER-07', N'Ver Seven', '+971507770007', 'v07@example.test', 8000107, 'V7-A', '20261010', 175, 'Installment');
GO
