import sql from 'mssql';
import { TEST_OID_PREFIX } from '../testUsers';
import { systemQuery } from './system';
import type { Role } from './withRlsContext';

const RESERVED_OID = /^00000000-0000-4000-8000-[0-9a-f]{12}$/i;

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

const BASE = `SELECT u.UserId, CONVERT(char(36), u.EntraOid) AS EntraOid, u.DisplayName, u.Email, u.Role, u.TeamId, t.Name AS TeamName
  FROM crm.Users u LEFT JOIN crm.Teams t ON t.TeamId = u.TeamId WHERE u.IsActive = 1`;
const RESERVED = `LOWER(CONVERT(char(36), u.EntraOid)) LIKE '${TEST_OID_PREFIX}%'`;

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
    // The seeded users plus any active user in the reserved test-oid range (the optional named Admin). Real Entra
    // object ids are random GUIDs and never fall in that range, so no real account can ever be offered here.
    const rows = await systemQuery<Raw>(`${BASE} AND (u.EntraOid IN (SELECT TRY_CAST([value] AS uniqueidentifier) FROM OPENJSON(@oids)) OR ${RESERVED}) ORDER BY u.UserId`, [{ name: 'oids', type: sql.NVarChar(sql.MAX), value: JSON.stringify(oids) }]);
    return rows.map(map);
  },
  async find(oid, allowedOids) {
    // Only a listed seed user or a reserved-range oid can be looked up; the query re-checks the range in SQL.
    if (!allowedOids.includes(oid) && !RESERVED_OID.test(oid)) return null;
    const rows = await systemQuery<Raw>(`${BASE} AND u.EntraOid = TRY_CAST(@oid AS uniqueidentifier) AND (u.EntraOid IN (SELECT TRY_CAST([value] AS uniqueidentifier) FROM OPENJSON(@oids)) OR ${RESERVED})`, [
      { name: 'oid', type: sql.VarChar(36), value: oid }, { name: 'oids', type: sql.NVarChar(sql.MAX), value: JSON.stringify(allowedOids) }]);
    return rows[0] ? map(rows[0]) : null;
  },
};
