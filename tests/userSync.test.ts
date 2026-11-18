import { describe, expect, it, vi } from 'vitest';
import type { IdClaims } from '../src/auth/msal';
import { logger } from '../src/logger';
import { applyTeamPolicy, extractGroups, syncUser } from '../src/auth/userSync';
import type { TeamStatus, UpsertUserInput, UserRepo } from '../src/db/userRepo';

const G1 = 'aaaaaaaa-0000-4000-8000-000000000001';
const G2 = 'aaaaaaaa-0000-4000-8000-000000000002';
const claims = (extra: Partial<IdClaims> = {}): IdClaims => ({
  oid: '11111111-1111-4111-8111-111111111111',
  tid: '22222222-2222-4222-8222-222222222222',
  name: 'Jane Doe',
  email: 'jane@example.com',
  roles: ['CRM.User'],
  ...extra,
});

describe('extractGroups', () => {
  it('returns the group ids, lower-cased, when there is no overage', () => {
    expect(extractGroups({ groups: [G1.toUpperCase(), G2] })).toEqual({ groupIds: [G1, G2], overage: false });
    expect(extractGroups({})).toEqual({ groupIds: [], overage: false });
  });
  it('detects overage via _claim_names.groups', () => {
    expect(extractGroups({ _claim_names: { groups: 'src1' } })).toEqual({ groupIds: [], overage: true });
  });
  it('detects overage via hasgroups with no groups claim', () => {
    expect(extractGroups({ hasgroups: true })).toEqual({ groupIds: [], overage: true });
    expect(extractGroups({ hasgroups: true, groups: [G1] }).overage).toBe(false);
  });
});

describe('applyTeamPolicy (exactly one linked team group)', () => {
  it('0 groups -> no team', () => {
    expect(applyTeamPolicy({ teamId: null, teamStatus: 'none', teamMatches: 0 }, 'oid')).toBeNull();
  });
  it('1 group -> that team', () => {
    expect(applyTeamPolicy({ teamId: 7, teamStatus: 'ok', teamMatches: 1 }, 'oid')).toBe(7);
  });
  it('2+ groups -> TeamId NULL, never "lowest id wins", warning logged', () => {
    const warn = vi.spyOn(logger, 'warn').mockImplementation(() => undefined);
    // even if a (buggy) database returned the lowest id, the app fails closed
    expect(applyTeamPolicy({ teamId: 3, teamStatus: 'multiple', teamMatches: 2 }, 'the-oid')).toBeNull();
    expect(warn).toHaveBeenCalledWith(expect.objectContaining({ oid: 'the-oid', teamMatches: 2 }), expect.stringContaining('several team groups'));
    warn.mockRestore();
  });
  it('overage -> TeamId NULL and an error is logged with the oid', () => {
    const error = vi.spyOn(logger, 'error').mockImplementation(() => undefined);
    expect(applyTeamPolicy({ teamId: 3, teamStatus: 'overage', teamMatches: 0 }, 'the-oid')).toBeNull();
    expect(error).toHaveBeenCalledWith(expect.objectContaining({ oid: 'the-oid' }), expect.any(String));
    error.mockRestore();
  });
  it('status ok with an inconsistent match count still fails closed', () => {
    expect(applyTeamPolicy({ teamId: 3, teamStatus: 'ok', teamMatches: 2 }, 'oid')).toBeNull();
  });
});

describe('syncUser', () => {
  const repoReturning = (r: { teamId: number | null; teamStatus: TeamStatus; teamMatches: number }) => {
    const upsert = vi.fn<(i: UpsertUserInput) => Promise<{ userId: number; teamId: number | null; teamStatus: TeamStatus; teamMatches: number }>>(async () => ({ userId: 42, ...r }));
    return { repo: { upsertOnLogin: upsert } as UserRepo, upsert };
  };

  it('passes groups and role to the stored procedure and builds the session user', async () => {
    const { repo, upsert } = repoReturning({ teamId: 5, teamStatus: 'ok', teamMatches: 1 });
    const user = await syncUser(repo, { claims: claims({ groups: [G1] }), role: 'Manager', tenantId: 'tid' });
    expect(upsert).toHaveBeenCalledWith(expect.objectContaining({ role: 'Manager', groupIds: [G1], groupsOverage: false, entraOid: claims().oid }));
    expect(user).toMatchObject({ userId: 42, role: 'Manager', teamId: 5, oid: claims().oid, name: 'Jane Doe' });
  });
  it('sends overage to the procedure with an empty group list', async () => {
    const { repo, upsert } = repoReturning({ teamId: null, teamStatus: 'overage', teamMatches: 0 });
    const user = await syncUser(repo, { claims: claims({ hasgroups: true }), role: 'User', tenantId: 'tid' });
    expect(upsert).toHaveBeenCalledWith(expect.objectContaining({ groupIds: [], groupsOverage: true }));
    expect(user.teamId).toBeNull();
  });
});
