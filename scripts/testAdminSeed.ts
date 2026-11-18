import type { TestAdmin } from '../src/config';

/** Upserts the optional named test Admin. All values are parameters; the oid is deterministic (see src/testUsers.ts). */
export const TEST_ADMIN_UPSERT_SQL = `
MERGE crm.Users AS u
USING (SELECT @oid AS EntraOid) AS s ON u.EntraOid = s.EntraOid
WHEN MATCHED THEN UPDATE SET DisplayName = @name, Email = @email, Role = 'Admin', TeamId = NULL, TeamStatus = 'none', IsActive = 1, ModifiedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (EntraOid, TenantId, DisplayName, Email, Role, TeamId, TeamStatus)
  VALUES (@oid, '00000000-0000-0000-0000-000000000000', @name, @email, 'Admin', NULL, 'none');`;

export const testAdminParams = (a: TestAdmin) => ({ oid: a.oid, name: a.name, email: a.email });
