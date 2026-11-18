import { describe, expect, it } from 'vitest';
import { hasAtLeast, mapRole } from '../src/auth/roles';

describe('mapRole', () => {
  it('maps single roles', () => {
    expect(mapRole(['CRM.Admin'])).toBe('Admin');
    expect(mapRole(['CRM.Manager'])).toBe('Manager');
    expect(mapRole(['CRM.User'])).toBe('User');
  });
  it('highest role wins regardless of order', () => {
    expect(mapRole(['CRM.User', 'CRM.Admin', 'CRM.Manager'])).toBe('Admin');
    expect(mapRole(['CRM.User', 'CRM.Manager'])).toBe('Manager');
  });
  it('ignores unknown roles and returns null when none match', () => {
    expect(mapRole(['Other.Role', 'crm.admin'])).toBeNull();
    expect(mapRole([])).toBeNull();
    expect(mapRole(undefined)).toBeNull();
    expect(mapRole('CRM.Admin')).toBeNull();
    expect(mapRole([1, null, 'CRM.User'])).toBe('User');
  });
});

describe('hasAtLeast', () => {
  it('orders Admin > Manager > User', () => {
    expect(hasAtLeast('Admin', 'Manager')).toBe(true);
    expect(hasAtLeast('Manager', 'Admin')).toBe(false);
    expect(hasAtLeast('User', 'User')).toBe(true);
    expect(hasAtLeast('User', 'Manager')).toBe(false);
  });
});
