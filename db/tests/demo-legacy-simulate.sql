-- db/tests/demo-legacy-simulate.sql
-- CI only, TEST DATABASES ONLY. Turns freshly seeded demo data into the "legacy" state the first seeder version left behind:
-- the demo.seeded marker stays, the demo.runId setting is removed. Used to exercise `seed-demo --purge-legacy-demo --dry-run`.
SET NOCOUNT ON;
EXEC sp_set_session_context @key = N'UserId', @value = 0, @read_only = 0;
EXEC sp_set_session_context @key = N'Role',   @value = N'Admin', @read_only = 0;
GO
DELETE FROM crm.Settings WHERE SettingKey = 'demo.runId';
GO
