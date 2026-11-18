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

/** The only write path to crm.Users: crm.usp_UpsertUserOnLogin (idempotent MERGE, so retry-safe). */
export const userRepo: UserRepo = {
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
