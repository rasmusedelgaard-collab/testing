-- db/tests/demo-purge-dryrun-check.sql
-- Run after `seed-demo --yes-demo --purge-legacy-demo --dry-run` on a database in the legacy state (demo-legacy-simulate.sql).
-- A dry run changes nothing: the demo rows and the marker are still there, and no purge audit row exists. Read-only.
SET NOCOUNT ON;
EXEC sp_set_session_context @key = N'UserId', @value = 0, @read_only = 0;
EXEC sp_set_session_context @key = N'Role',   @value = N'Admin', @read_only = 0;
GO
SELECT 'P1' AS [Case], CASE WHEN COUNT(*) = 1 THEN 'PASS' ELSE 'FAIL' END AS Result,
  'Dry run left the demo.seeded marker in place' AS Description, CONCAT('rows=', COUNT(*)) AS Detail
FROM crm.Settings WHERE SettingKey = 'demo.seeded';
GO
SELECT 'P2' AS [Case], CASE WHEN COUNT(*) > 0 THEN 'PASS' ELSE 'FAIL' END AS Result,
  'Dry run left the demo accounts in place' AS Description, CONCAT('accounts=', COUNT(*)) AS Detail
FROM crm.Accounts WHERE CreatedBy IN (SELECT UserId FROM crm.Users WHERE LOWER(CONVERT(char(36), EntraOid)) LIKE '00000000-0000-4000-8000-00000000d0%');
GO
SELECT 'P3' AS [Case], CASE WHEN COUNT(*) = 0 THEN 'PASS' ELSE 'FAIL' END AS Result,
  'Dry run wrote no purge audit row' AS Description, CONCAT('rows=', COUNT(*)) AS Detail
FROM crm.AuditLog WHERE EntityType = 'setting' AND Action = 'D' AND NewValue LIKE N'Legacy demo purge:%';
GO
