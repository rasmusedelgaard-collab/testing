import type { Role } from './types';

const APP_ROLES: ReadonlyArray<readonly [string, Role]> = [
  ['CRM.Admin', 'Admin'],
  ['CRM.Manager', 'Manager'],
  ['CRM.User', 'User'],
];

/** Highest app role wins. Unknown values are ignored. null means "no CRM access". */
export function mapRole(roles: unknown): Role | null {
  if (!Array.isArray(roles)) return null;
  const present = new Set(roles.filter((r): r is string => typeof r === 'string'));
  for (const [claim, role] of APP_ROLES) if (present.has(claim)) return role;
  return null;
}

const RANK: Record<Role, number> = { User: 1, Manager: 2, Admin: 3 };
export function hasAtLeast(actual: Role, required: Role): boolean {
  return RANK[actual] >= RANK[required];
}
