-- db/seed/test-users.sql: seeded users for AUTH_MODE=test-access-code (interim hosted TEST site only).
-- TEST DATA ONLY. Never run against a database that holds real data. Idempotent.
-- Run with:  npm run db:seed:test   (as the deployer / DB owner login)
-- The fixed EntraOid values must match TEST_USERS in src/auth/testLogin.ts.

EXEC sp_set_session_context @key = N'UserId', @value = 0;
EXEC sp_set_session_context @key = N'Role',   @value = N'Admin';

MERGE crm.Teams AS t
USING (VALUES
  (N'Test Team A', '00000000-0000-4000-8000-0000000fa001'),
  (N'Test Team B', '00000000-0000-4000-8000-0000000fb001')
) AS s (Name, EntraGroupId) ON t.EntraGroupId = s.EntraGroupId
WHEN NOT MATCHED THEN INSERT (Name, EntraGroupId) VALUES (s.Name, s.EntraGroupId);

EXEC sp_set_session_context @key = N'UserId', @value = NULL;
EXEC sp_set_session_context @key = N'Role',   @value = NULL;

DECLARE @TeamA int = (SELECT TeamId FROM crm.Teams WHERE EntraGroupId = '00000000-0000-4000-8000-0000000fa001');
DECLARE @TeamB int = (SELECT TeamId FROM crm.Teams WHERE EntraGroupId = '00000000-0000-4000-8000-0000000fb001');
DECLARE @Tenant uniqueidentifier = '00000000-0000-0000-0000-000000000000';

MERGE crm.Users AS u
USING (VALUES
  ('00000000-0000-4000-8000-00000000a001', N'Test Admin',        N'admin@test.invalid',     'Admin',   CAST(NULL AS int), 'none'),
  ('00000000-0000-4000-8000-00000000a002', N'Test Manager A',    N'manager.a@test.invalid', 'Manager', @TeamA, 'ok'),
  ('00000000-0000-4000-8000-00000000a003', N'Test User A1',      N'user.a1@test.invalid',   'User',    @TeamA, 'ok'),
  ('00000000-0000-4000-8000-00000000a004', N'Test User A2',      N'user.a2@test.invalid',   'User',    @TeamA, 'ok'),
  ('00000000-0000-4000-8000-00000000b001', N'Test Manager B',    N'manager.b@test.invalid', 'Manager', @TeamB, 'ok'),
  ('00000000-0000-4000-8000-00000000b002', N'Test User B1',      N'user.b1@test.invalid',   'User',    @TeamB, 'ok'),
  ('00000000-0000-4000-8000-00000000c001', N'Test User No Team', N'user.none@test.invalid', 'User',    CAST(NULL AS int), 'none')
) AS s (EntraOid, DisplayName, Email, Role, TeamId, TeamStatus) ON u.EntraOid = s.EntraOid
WHEN MATCHED THEN UPDATE SET DisplayName = s.DisplayName, Email = s.Email, Role = s.Role, TeamId = s.TeamId,
     TeamStatus = s.TeamStatus, IsActive = 1, ModifiedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (EntraOid, TenantId, DisplayName, Email, Role, TeamId, TeamStatus)
     VALUES (s.EntraOid, @Tenant, s.DisplayName, s.Email, s.Role, s.TeamId, s.TeamStatus);
GO
