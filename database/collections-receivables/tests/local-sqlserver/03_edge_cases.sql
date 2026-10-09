-- Edge cases for the Campaigns equivalence tests (synthetic, NOT PACT data). Run after 01_fake_pact.sql, then refresh the snapshot.
-- Every tenant is named EDGE-nn so the cases are easy to find; dates are chosen for a preview date of 2026-10-09:
--   Overdue stage: due before 2026-09-09 | Legal notice: September 2026 (> 1500) | Current month / follow-up: October 2026 | Legal referral: before 2026-07-09 (> 20000)
USE PACTRPT;
GO
DELETE dbo.FakeEdgeRows;
DECLARE @mar datetime = '20260305', @sep datetime = '20260915', @oct datetime = '20261014';
INSERT dbo.FakeEdgeRows (CompanyId, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, DueDate, Amount, Status)
VALUES
-- 01 ambiguous: two instalments of one unit on the same due date
(4, '', 'TP101-9001', 'EDGE-01', N'Edge One',  '+971501110001', 'e01@example.test', 9000001, 'V-01-1', @mar, 500, 'Installment'),
(4, '', 'TP101-9001', 'EDGE-01', N'Edge One',  '+971501110001', 'e01@example.test', 9000001, 'V-01-2', @mar, 500, 'Installment'),
-- 02 ambiguous with a first-row tie-break: same day, the displayed contact is the smallest voucher ('A-1'), not the first listed
(4, '', 'TP101-9002', 'EDGE-02', N'Zed Second', '0502220002', 'zed@example.test', 9000002, 'B-2', @mar, 400, 'Installment'),
(4, '', 'TP101-9002', 'EDGE-02', N'Amy First',  '+971502220009', 'amy@example.test', 9000002, 'A-1', @mar, 400, 'Installment'),
-- 03 / 04 contradictory payment status (Paid with a positive remaining amount), any letter case
(4, '', 'TP101-9003', 'EDGE-03', N'Edge Three', '+971501110003', 'e03@example.test', 9000003, 'V-03', @mar, 700, 'Paid'),
(4, '', 'TP101-9004', 'EDGE-04', N'Edge Four',  '+971501110004', 'e04@example.test', 9000004, 'V-04', @mar, 700, 'PAID'),
-- 05 one tenant, two units, the same due date (unit allocation needs review)
(4, '', 'TP102-9051', 'EDGE-05', N'Edge Five',  '+971501110005', 'e05@example.test', 9000051, 'V-05-1', @mar, 800, 'Installment'),
(4, '', 'TP102-9052', 'EDGE-05', N'Edge Five',  '+971501110005', 'e05@example.test', 9000052, 'V-05-2', @mar, 800, 'Installment'),
-- 06 one unit id with two unit codes; 07 one unit code with two unit ids (different days)
(4, '', 'TP102-9061',  'EDGE-06', N'Edge Six',   '+971501110006', 'e06@example.test', 9000061, 'V-06-1', '20260301', 600, 'Installment'),
(4, '', 'TP102-9061X', 'EDGE-06', N'Edge Six',   '+971501110006', 'e06@example.test', 9000061, 'V-06-2', '20260401', 600, 'Installment'),
(4, '', 'TP102-9071',  'EDGE-07', N'Edge Seven', '+971501110007', 'e07@example.test', 9000071, 'V-07-1', '20260301', 650, 'Installment'),
(4, '', 'TP102-9071',  'EDGE-07', N'Edge Seven', '+971501110007', 'e07@example.test', 9000072, 'V-07-2', '20260401', 650, 'Installment'),
-- 08 phones that differ in text but normalise to the same number: NOT a conflict
(4, '', 'TP103-9081', 'EDGE-08', N'Edge Eight', '0501234567',      'e08@example.test', 9000081, 'V-08-1', '20260301', 300, 'Installment'),
(4, '', 'TP103-9081', 'EDGE-08', N'Edge Eight', '+971501234567',   'e08@example.test', 9000081, 'V-08-2', '20260401', 300, 'Installment'),
(4, '', 'TP103-9081', 'EDGE-08', N'Edge Eight', '971501234567',    'e08@example.test', 9000081, 'V-08-3', '20260501', 300, 'Installment'),
(4, '', 'TP103-9081', 'EDGE-08', N'Edge Eight', ' 050 123 4567 ',  'e08@example.test', 9000081, 'V-08-4', '20260601', 300, 'Installment'),
-- 09 names that differ only by letter case: conflict (ordinal comparison)
(4, '', 'TP103-9091', 'EDGE-09', N'john doe', '+971501110009', 'e09@example.test', 9000091, 'V-09-1', '20260301', 310, 'Installment'),
(4, '', 'TP103-9091', 'EDGE-09', N'JOHN DOE', '+971501110009', 'e09@example.test', 9000091, 'V-09-2', '20260401', 310, 'Installment'),
-- 10 names that differ only by surrounding spaces: NOT a conflict (trimmed)
(4, '', 'TP103-9101', 'EDGE-10', N' Jane Roe', '+971501110010', 'e10@example.test', 9000101, 'V-10-1', '20260301', 320, 'Installment'),
(4, '', 'TP103-9101', 'EDGE-10', N'Jane Roe ', '+971501110010', 'e10@example.test', 9000101, 'V-10-2', '20260401', 320, 'Installment'),
-- 11 project codes that differ only by a trailing space: conflict (the project code is compared as is)
(4, 'P1',  'TP103-9111', 'EDGE-11', N'Edge Eleven', '+971501110011', 'e11@example.test', 9000111, 'V-11-1', '20260301', 330, 'Installment'),
(4, 'P1 ', 'TP103-9111', 'EDGE-11', N'Edge Eleven', '+971501110011', 'e11@example.test', 9000111, 'V-11-2', '20260401', 330, 'Installment'),
-- 12 a valid and an invalid e-mail on one unit: conflict
(4, '', 'TP103-9121', 'EDGE-12', N'Edge Twelve', '+971501110012', 'ok12@example.test', 9000121, 'V-12-1', '20260301', 340, 'Installment'),
(4, '', 'TP103-9121', 'EDGE-12', N'Edge Twelve', '+971501110012', 'not-an-email',      9000121, 'V-12-2', '20260401', 340, 'Installment'),
-- 13 invalid phone and invalid e-mail: no valid contact; 14 blank phone and e-mail; 15 valid phone, invalid e-mail; 16 invalid phone, valid e-mail
(4, '', 'TP104-9131', 'EDGE-13', N'Edge Thirteen', '123',          'bad',              9000131, 'V-13', @mar, 350, 'Installment'),
(4, '', 'TP104-9141', 'EDGE-14', N'Edge Fourteen', '',             '',                 9000141, 'V-14', @mar, 350, 'Installment'),
(4, '', 'TP104-9151', 'EDGE-15', N'Edge Fifteen',  '00971501110015', 'Name <x@y.test>', 9000151, 'V-15', @mar, 350, 'Installment'),
(4, '', 'TP104-9161', 'EDGE-16', N'Edge Sixteen',  '0123456789',    'e16@example.test', 9000161, 'V-16', @mar, 350, 'Installment'),
-- 17 amount precision beyond two decimals
(4, '', 'TP104-9171', 'EDGE-17', N'Edge Seventeen', '+971501110017', 'e17@example.test', 9000171, 'V-17', @mar, 1234.567, 'Installment'),
-- 18 tenant ids that differ only by letter case are different customers; 19 tenant id with surrounding spaces (trimmed on publish)
(4, '', 'TP104-9181', 'edge-18', N'Edge Eighteen lower', '+971501110018', 'e18a@example.test', 9000181, 'V-18-1', @mar, 360, 'Installment'),
(4, '', 'TP104-9181', 'EDGE-18', N'Edge Eighteen upper', '+971501110118', 'e18b@example.test', 9000181, 'V-18-2', @mar, 360, 'Installment'),
(4, '', 'TP104-9191', ' EDGE-19 ', N'Edge Nineteen', '+971501110019', 'e19@example.test', 9000191, 'V-19', @mar, 370, 'Installment'),
-- 20 unit code '0' and a blank unit code (missing unit identity)
(4, '', '0', 'EDGE-20', N'Edge Twenty zero', '+971501110020', 'e20a@example.test', 9000201, 'V-20-1', @mar, 380, 'Installment'),
(4, '', '',  'EDGE-21', N'Edge Twenty-one blank', '+971501110021', 'e21@example.test', 9000211, 'V-21', @mar, 380, 'Installment'),
-- 22 minimum-amount boundary: exactly 100, 99.99 and 100.01 remaining
(4, '', 'TP105-9221', 'EDGE-22', N'Edge Min exact', '+971501110022', 'e22a@example.test', 9000221, 'V-22-1', @mar, 100, 'Installment'),
(4, '', 'TP105-9222', 'EDGE-23', N'Edge Min below', '+971501110023', 'e23@example.test', 9000222, 'V-23', @mar, 99.99, 'Installment'),
(4, '', 'TP105-9223', 'EDGE-24', N'Edge Min above', '+971501110024', 'e24@example.test', 9000223, 'V-24', @mar, 100.01, 'Installment'),
-- 25 legal-notice threshold (> 1500) in September: exactly 1500 (no), 1500.01 (yes), 1499.99 (no), and two instalments summing above it
(4, '', 'TP105-9251', 'EDGE-25', N'Edge Notice 1500',    '+971501110025', 'e25@example.test', 9000251, 'V-25', @sep, 1500, 'Installment'),
(4, '', 'TP105-9261', 'EDGE-26', N'Edge Notice 1500.01', '+971501110026', 'e26@example.test', 9000261, 'V-26', @sep, 1500.01, 'Installment'),
(4, '', 'TP105-9271', 'EDGE-27', N'Edge Notice 1499.99', '+971501110027', 'e27@example.test', 9000271, 'V-27', @sep, 1499.99, 'Installment'),
(4, '', 'TP105-9281', 'EDGE-28', N'Edge Notice sum',     '+971501110028', 'e28@example.test', 9000281, 'V-28-1', '20260902', 800, 'Installment'),
(4, '', 'TP105-9281', 'EDGE-28', N'Edge Notice sum',     '+971501110028', 'e28@example.test', 9000281, 'V-28-2', '20260920', 800, 'Installment'),
-- 29 legal-referral threshold (> 20000, due before 2026-07-09)
(4, '', 'TP105-9291', 'EDGE-29', N'Edge Referral 20000',    '+971501110029', 'e29@example.test', 9000291, 'V-29', '20260105', 20000, 'Installment'),
(4, '', 'TP105-9301', 'EDGE-30', N'Edge Referral 20000.01', '+971501110030', 'e30@example.test', 9000301, 'V-30', '20260105', 20000.01, 'Installment'),
(4, '', 'TP105-9311', 'EDGE-31', N'Edge Referral sum',      '+971501110031', 'e31@example.test', 9000311, 'V-31-1', '20260105', 12000, 'Installment'),
(4, '', 'TP105-9311', 'EDGE-31', N'Edge Referral sum',      '+971501110031', 'e31@example.test', 9000311, 'V-31-2', '20260205', 9000, 'Installment'),
-- 32 search patterns: LIKE wildcards in names and e-mails, brackets, apostrophe, Arabic text
(4, '', 'TP106-9321', 'EDGE-32', N'O''Brien_100% [Test]', '+971501110032', 'o_brien@example.test', 9000321, 'V-32', @mar, 390, 'Installment'),
(4, '', 'TP106-9331', 'EDGE-33', N'عميل تجريبي',          '+971501110033', 'e33@example.test', 9000331, 'V-33', @mar, 390, 'Installment'),
-- 34 current-month (October) candidates with unit-level flags: ambiguous, contradictory, conflicting phones, no contact
(4, '', 'TP106-9341', 'EDGE-34', N'Edge Oct ambiguous', '+971501110034', 'e34@example.test', 9000341, 'V-34-1', @oct, 450, 'Installment'),
(4, '', 'TP106-9341', 'EDGE-34', N'Edge Oct ambiguous', '+971501110034', 'e34@example.test', 9000341, 'V-34-2', @oct, 450, 'Installment'),
(4, '', 'TP106-9351', 'EDGE-35', N'Edge Oct paid-status', '+971501110035', 'e35@example.test', 9000351, 'V-35', @oct, 450, 'Paid'),
(4, '', 'TP106-9361', 'EDGE-36', N'Edge Oct conflict',  '+971501110036', 'e36@example.test', 9000361, 'V-36-1', '20261003', 450, 'Installment'),
(4, '', 'TP106-9361', 'EDGE-36', N'Edge Oct conflict',  '+971501119999', 'e36@example.test', 9000361, 'V-36-2', '20261020', 450, 'Installment'),
(4, '', 'TP106-9371', 'EDGE-37', N'Edge Oct no contact', '', '', 9000371, 'V-37', @oct, 450, 'Installment'),
-- 38 a unit with an overdue row (March) and a current-month row (October) with another phone: the flag comes from ALL window rows, so it shows in both stages
(4, '', 'TP106-9381', 'EDGE-38', N'Edge Both stages', '+971501110038', 'e38@example.test', 9000381, 'V-38-1', @mar, 500, 'Installment'),
(4, '', 'TP106-9381', 'EDGE-38', N'Edge Both stages', '+971501110099', 'e38@example.test', 9000381, 'V-38-2', @oct, 500, 'Installment'),
-- 39 a 'Paid' status only on a row that is not in the overdue stage range: contradictory flag still applies to the unit
(4, '', 'TP106-9391', 'EDGE-39', N'Edge Paid elsewhere', '+971501110039', 'e39@example.test', 9000391, 'V-39-1', @mar, 500, 'Installment'),
(4, '', 'TP106-9391', 'EDGE-39', N'Edge Paid elsewhere', '+971501110039', 'e39@example.test', 9000391, 'V-39-2', @oct, 500, 'Paid'),
-- company 32 (Faradis / Al Ghaf towers): an ambiguous unit and a conflicting one
(32, '', 'TP127-9401', 'EDGE-40', N'Edge Faradis ambiguous', '+971501110040', 'e40@example.test', 9000401, 'V-40-1', @mar, 500, 'Installment'),
(32, '', 'TP127-9401', 'EDGE-40', N'Edge Faradis ambiguous', '+971501110040', 'e40@example.test', 9000401, 'V-40-2', @mar, 500, 'Installment'),
(32, '', 'TP140-9411', 'EDGE-41', N'Edge Al Ghaf conflict', '+971501110041', 'e41@example.test', 9000411, 'V-41-1', '20260301', 500, 'Installment'),
(32, '', 'TP140-9411', 'EDGE-41', N'Edge Al Ghaf conflict', '+971501110141', 'e41@example.test', 9000411, 'V-41-2', '20260401', 500, 'Installment');
PRINT CONCAT('FakeEdgeRows: ', @@ROWCOUNT, ' rows');
GO
