import { createHash } from 'node:crypto';

/** Every seeded/test user has an EntraOid in this reserved range; real Entra object ids are random GUIDs and never match. */
export const TEST_OID_PREFIX = '00000000-0000-4000-8000-';

/**
 * Deterministic object id for the optional named test Admin (TEST_ADMIN_NAME/TEST_ADMIN_EMAIL), derived from the
 * lower-cased email so seeding twice updates the same row. Nothing personal is stored in the repo.
 */
export function testAdminOid(email: string): string {
  return `${TEST_OID_PREFIX}${createHash('sha256').update(`crm-test-admin:${email.trim().toLowerCase()}`).digest('hex').slice(0, 12)}`;
}
