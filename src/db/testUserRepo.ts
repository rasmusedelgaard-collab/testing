import sql from 'mssql';
import { systemQuery } from './system';
import type { Role } from './withRlsContext';

export interface TestUserRow {
  userId: number;
  oid: string;
  name: string;
  email: string | null;
  role: Role;
  teamId: number | null;
  teamName: string | null;
}

export interface TestUserRepo {
  /** Only rows whose EntraOid is in `oids`, and active. */
  list(oids: readonly string[]): Promise<TestUserRow[]>;
  find(oid: string, allowedOids: readonly string[]): Promise<TestUserRow | null>;
}

interface Raw {
  UserId: number;
  EntraOid: string;
  DisplayName: string;
  Email: string | null;
  Role: Role;
  TeamId: number | null;
  TeamName: string | null;
}

const SELECT = `SELECT u.UserId, CONVERT(char(36), u.EntraOid) AS EntraOid, u.DisplayName, u.Email, u.Role, u.TeamId, t.Name AS TeamName
  FROM crm.Users u LEFT JOIN crm.Teams t ON t.TeamId = u.TeamId
  WHERE u.IsActive = 1 AND u.EntraOid IN (SELECT TRY_CAST([value] AS uniqueidentifier) FROM OPENJSON(@oids))`;

const map = (r: Raw): TestUserRow => ({
  userId: r.UserId,
  oid: r.EntraOid.toLowerCase(),
  name: r.DisplayName,
  email: r.Email,
  role: r.Role,
  teamId: r.TeamId,
  teamName: r.TeamName,
});

export const testUserRepo: TestUserRepo = {
  async list(oids) {
    const rows = await systemQuery<Raw>(`${SELECT} ORDER BY u.UserId`, [{ name: 'oids', type: sql.NVarChar(sql.MAX), value: JSON.stringify(oids) }]);
    return rows.map(map);
  },
  async find(oid, allowedOids) {
    if (!allowedOids.includes(oid)) return null;
    const rows = await this.list([oid]);
    return rows[0] ?? null;
  },
};
