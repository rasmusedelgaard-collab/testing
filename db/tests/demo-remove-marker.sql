-- db/tests/demo-remove-marker.sql
-- CI only, TEST DATABASES ONLY. Removes the demo.seeded marker (and the run id), to check that --purge-legacy-demo refuses without a marker.
SET NOCOUNT ON;
EXEC sp_set_session_context @key = N'UserId', @value = 0, @read_only = 0;
EXEC sp_set_session_context @key = N'Role',   @value = N'Admin', @read_only = 0;
GO
DELETE FROM crm.Settings WHERE SettingKey IN ('demo.seeded', 'demo.runId');
GO
