import sql from 'mssql';
import { systemQuery } from './system';

export interface UpsertUserInput {
  entraOid: string;
  tenantId: string;
  displayName: string;
  email: string | null;
  role: 'Admin' | 'Manager' | 'User';
  groupIds: string[];
  groupsOverage: boolean;
}

export type TeamStatus = 'ok' | 'none' | 'multiple' | 'overage';
export interface UpsertUserResult {
  userId: number;
  teamId: number | null;
  teamStatus: TeamStatus;
  teamMatches: number;
}

export interface UserRepo {
  upsertOnLogin(input: UpsertUserInput): Promise<UpsertUserResult>;
}

/** Error number raised by crm.usp_UpsertUserOnLogin for a deactivated user. */
export const USER_DEACTIVATED = 50011;

/** The only write path to crm.Users: crm.usp_UpsertUserOnLogin (idempotent MERGE, so retry-safe). */
/** Is the user still active? A missing row counts as inactive. Read-only and idempotent. */
export const userRepo: UserRepo & { isActive(userId: number): Promise<boolean> } = {
  async isActive(userId) {
    const rows = await systemQuery<{ IsActive: boolean }>('SELECT IsActive FROM crm.Users WHERE UserId = @id', [{ name: 'id', type: sql.Int, value: userId }]);
    return rows[0]?.IsActive === true;
  },
  async upsertOnLogin(i) {
    const rows = await systemQuery<{ UserId: number; TeamId: number | null; TeamStatus: TeamStatus; TeamMatches: number }>(
      `EXEC crm.usp_UpsertUserOnLogin @EntraOid = @EntraOid, @TenantId = @TenantId, @DisplayName = @DisplayName,
         @Email = @Email, @Role = @Role, @GroupIdsJson = @GroupIdsJson, @GroupsOverage = @GroupsOverage`,
      [
        { name: 'EntraOid', type: sql.UniqueIdentifier, value: i.entraOid },
        { name: 'TenantId', type: sql.UniqueIdentifier, value: i.tenantId },
        { name: 'DisplayName', type: sql.NVarChar(200), value: i.displayName },
        { name: 'Email', type: sql.NVarChar(254), value: i.email },
        { name: 'Role', type: sql.VarChar(10), value: i.role },
        { name: 'GroupIdsJson', type: sql.NVarChar(sql.MAX), value: JSON.stringify(i.groupIds) },
        { name: 'GroupsOverage', type: sql.Bit, value: i.groupsOverage },
      ],
    );
    const row = rows[0];
    if (!row) throw new Error('usp_UpsertUserOnLogin returned no row');
    return { userId: row.UserId, teamId: row.TeamId, teamStatus: row.TeamStatus, teamMatches: row.TeamMatches };
  },
};
