import request from 'supertest';
import { describe, expect, it, vi } from 'vitest';
import type { IdClaims, OidcClient } from '../src/auth/msal';
import type { UserRepo } from '../src/db/userRepo';
import { allSessions, entraConfig, GUID, makeApp } from './helpers';

const OID = '99999999-9999-4999-8999-999999999999';

function setup(claimsOverride: Partial<IdClaims> | ((nonce: string) => Partial<IdClaims>) = {}) {
  let lastAuth: { state: string; nonce: string; codeChallenge: string } | undefined;
  const oidc: OidcClient = {
    authUrl: vi.fn(async (p) => {
      lastAuth = p;
      return `https://login.microsoftonline.com/tenant/oauth2/v2.0/authorize?state=${p.state}&code_challenge=${p.codeChallenge}&response_mode=query`;
    }),
    redeem: vi.fn(async () => {
      const extra = typeof claimsOverride === 'function' ? claimsOverride(lastAuth!.nonce) : claimsOverride;
      return { oid: OID, tid: GUID, nonce: lastAuth!.nonce, name: 'Jane Doe', email: 'jane@example.com', roles: ['CRM.User'], ...extra } as IdClaims;
    }),
    logoutUrl: () => 'https://login.microsoftonline.com/tenant/oauth2/v2.0/logout?post_logout_redirect_uri=http%3A%2F%2Flocalhost%3A3000%2Fsigned-out',
  };
  const users: UserRepo = {
    upsertOnLogin: vi.fn(async () => ({ userId: 12, teamId: 4, teamStatus: 'ok' as const, teamMatches: 1 })),
  };
  const { app, store } = makeApp({ config: entraConfig(), oidc, userRepo: users });
  return { app, store, oidc, users, auth: () => lastAuth! };
}

async function begin(agent: ReturnType<typeof request.agent>, returnTo?: string) {
  const res = await agent.get('/auth/login' + (returnTo ? `?returnTo=${encodeURIComponent(returnTo)}` : ''));
  expect(res.status).toBe(302);
  return new URL(res.headers.location as string).searchParams;
}

describe('Entra sign-in flow', () => {
  it('redirects to the authorize endpoint with state, S256 challenge and response_mode=query', async () => {
    const { app, auth } = setup();
    const params = await begin(request.agent(app));
    expect(params.get('response_mode')).toBe('query');
    expect(params.get('state')).toBe(auth().state);
    expect(params.get('code_challenge')).toBe(auth().codeChallenge);
    expect(auth().nonce).toHaveLength(43);
  });

  it('completes login: upserts the user, regenerates the session, returns to returnTo', async () => {
    const { app, users, store, auth } = setup({ groups: ['aaaaaaaa-0000-4000-8000-000000000001'] });
    const agent = request.agent(app);
    await begin(agent, '/accounts');
    const sessionsBefore = Object.keys(await allSessions(store));
    const res = await agent.get(`/auth/redirect?code=abc&state=${auth().state}`);
    expect(res.status).toBe(303);
    expect(res.headers.location).toBe('/accounts');
    expect(users.upsertOnLogin).toHaveBeenCalledWith(expect.objectContaining({ entraOid: OID, role: 'User', groupIds: ['aaaaaaaa-0000-4000-8000-000000000001'] }));
    const sessions = await allSessions(store);
    expect(Object.keys(sessions)).not.toEqual(sessionsBefore); // regenerated: new session id
    const [data] = Object.values(sessions);
    expect(data?.user).toMatchObject({ userId: 12, oid: OID, role: 'User', teamId: 4 });
    expect(data?.auth).toBeUndefined(); // transient login state removed
    expect(data?.csrfToken).toBeTruthy();
    expect((await agent.get('/')).status).toBe(200);
  });

  it('highest app role wins', async () => {
    const { app, users, auth } = setup({ roles: ['CRM.User', 'CRM.Admin'] });
    const agent = request.agent(app);
    await begin(agent);
    await agent.get(`/auth/redirect?code=abc&state=${auth().state}`);
    expect(users.upsertOnLogin).toHaveBeenCalledWith(expect.objectContaining({ role: 'Admin' }));
  });

  it('rejects a wrong state without redeeming the code', async () => {
    const { app, oidc } = setup();
    const agent = request.agent(app);
    await begin(agent);
    const res = await agent.get('/auth/redirect?code=abc&state=forged');
    expect(res.status).toBe(400);
    expect(oidc.redeem).not.toHaveBeenCalled();
  });

  it('rejects a callback with no pending login (e.g. replayed)', async () => {
    const { app, auth } = setup();
    const agent = request.agent(app);
    await begin(agent);
    await agent.get(`/auth/redirect?code=abc&state=${auth().state}`); // first use succeeds
    const replay = await request(app).get(`/auth/redirect?code=abc&state=${auth().state}`);
    expect(replay.status).toBe(400);
  });

  it('rejects a token for another tenant', async () => {
    const { app, users, auth } = setup({ tid: '00000000-0000-4000-8000-000000000000' });
    const agent = request.agent(app);
    await begin(agent);
    const res = await agent.get(`/auth/redirect?code=abc&state=${auth().state}`);
    expect(res.status).toBe(403);
    expect(users.upsertOnLogin).not.toHaveBeenCalled();
  });

  it('rejects a nonce mismatch', async () => {
    const { app, users, auth } = setup({ nonce: 'someone-elses-nonce' });
    const agent = request.agent(app);
    await begin(agent);
    const res = await agent.get(`/auth/redirect?code=abc&state=${auth().state}`);
    expect(res.status).toBe(400);
    expect(users.upsertOnLogin).not.toHaveBeenCalled();
  });

  it('a valid Entra user without any CRM app role gets the 403 no-access page and no DB write', async () => {
    const { app, users, auth, store } = setup({ roles: ['Some.Other.Role'] });
    const agent = request.agent(app);
    await begin(agent);
    const res = await agent.get(`/auth/redirect?code=abc&state=${auth().state}`);
    expect(res.status).toBe(303);
    expect(res.headers.location).toBe('/no-access');
    const page = await agent.get('/no-access');
    expect(page.status).toBe(403);
    expect(page.text).toContain("You don't have access to the CRM yet");
    expect(users.upsertOnLogin).not.toHaveBeenCalled();
    expect(Object.values(await allSessions(store)).some((s) => s.user)).toBe(false);
    expect((await agent.get('/')).status).toBe(303); // still not signed in
  });

  it('an identity-provider error returns the sign-in problem page', async () => {
    const { app } = setup();
    const agent = request.agent(app);
    await begin(agent);
    expect((await agent.get('/auth/redirect?error=access_denied')).status).toBe(400);
  });

  it('refuses to follow an open-redirect returnTo', async () => {
    const { app, auth } = setup();
    const agent = request.agent(app);
    await begin(agent, '//evil.example.com/x');
    const res = await agent.get(`/auth/redirect?code=abc&state=${auth().state}`);
    expect(res.headers.location).toBe('/');
  });

  it('sign-out clears the session and redirects to the Entra logout endpoint', async () => {
    const { app, auth } = setup();
    const agent = request.agent(app);
    await begin(agent);
    await agent.get(`/auth/redirect?code=abc&state=${auth().state}`);
    const page = await agent.get('/');
    const csrf = /name="_csrf" value="([^"]+)"/.exec(page.text)![1];
    const out = await agent.post('/auth/signout').type('form').send({ _csrf: csrf });
    expect(out.status).toBe(303);
    expect(out.headers.location).toContain('login.microsoftonline.com');
    expect(out.headers.location).toContain('post_logout_redirect_uri');
    expect((await agent.get('/')).status).toBe(303);
  });

  it('sign-out without a CSRF token is refused', async () => {
    const { app, auth } = setup();
    const agent = request.agent(app);
    await begin(agent);
    await agent.get(`/auth/redirect?code=abc&state=${auth().state}`);
    expect((await agent.post('/auth/signout').type('form').send({})).status).toBe(403);
    expect((await agent.get('/')).status).toBe(200);
  });
});
