import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { demoConfig, getCsrf, makeApp } from './helpers';

async function signedIn(role: 'Admin' | 'Manager' | 'User') {
  const { app } = makeApp({ config: demoConfig() });
  const agent = request.agent(app);
  const csrf = await getCsrf(agent, '/signin');
  const res = await agent.post('/demo/login').type('form').send({ _csrf: csrf, role });
  expect(res.status).toBe(303);
  return agent;
}

describe('public endpoints and security headers', () => {
  const { app } = makeApp({ config: demoConfig() });

  it('/healthz needs no session and no database', async () => {
    const res = await request(app).get('/healthz');
    expect(res.status).toBe(200);
    expect(res.text).toBe('ok');
    expect(res.headers['set-cookie']).toBeUndefined();
  });

  it('/readyz reflects the database check', async () => {
    const up = makeApp({ config: demoConfig(), ping: async () => true }).app;
    const down = makeApp({ config: demoConfig(), ping: async () => false }).app;
    expect((await request(up).get('/readyz')).status).toBe(200);
    const res = await request(down).get('/readyz');
    expect(res.status).toBe(503);
    expect(res.text).toBe('fail');
  });

  it('sends a strict CSP without inline script or style', async () => {
    const csp = (await request(app).get('/signin')).headers['content-security-policy'] as string;
    expect(csp).toContain("script-src 'self'");
    expect(csp).toContain("style-src 'self'");
    expect(csp).not.toContain('unsafe-inline');
    expect(csp).toContain("object-src 'none'");
    expect(csp).toContain("frame-ancestors 'none'");
    expect(csp).toContain('form-action');
    expect(csp).toContain('https://login.microsoftonline.com');
    expect((await request(app).get('/signin')).headers['x-powered-by']).toBeUndefined();
  });

  it('renders pages with no inline scripts, inline styles or event handlers', async () => {
    for (const path of ['/signin', '/signed-out', '/no-access']) {
      const html = (await request(app).get(path)).text;
      expect(html).not.toMatch(/<script(?![^>]*\ssrc=)/i);
      expect(html).not.toMatch(/\sstyle=/i);
      expect(html).not.toMatch(/\son[a-z]+=/i);
    }
  });

  it('serves the base stylesheet with the design tokens', async () => {
    const res = await request(app).get('/css/app.css');
    expect(res.status).toBe(200);
    expect(res.text).toContain('--color-primary: #1d4ed8');
  });
});

describe('authentication gate', () => {
  const { app } = makeApp({ config: demoConfig() });

  it('redirects unauthenticated GETs to /signin with returnTo', async () => {
    const res = await request(app).get('/accounts?x=1');
    expect(res.status).toBe(303);
    expect(res.headers.location).toBe('/signin?returnTo=%2Faccounts%3Fx%3D1');
  });

  it('redirects unauthenticated POSTs of the app area to /signin after the CSRF gate', async () => {
    const agent = request.agent(app);
    const csrf = await getCsrf(agent, '/signin');
    const res = await agent.post('/accounts').type('form').send({ _csrf: csrf });
    expect(res.status).toBe(303);
    expect(res.headers.location).toBe('/signin');
  });
});

describe('CSRF', () => {
  const { app } = makeApp({ config: demoConfig() });
  it('rejects a POST without a token with the 403 page', async () => {
    const res = await request(app).post('/demo/login').type('form').send({ role: 'Admin' });
    expect(res.status).toBe(403);
    expect(res.text).toContain("You don&#39;t have access to this page.");
  });
  it('rejects a wrong token', async () => {
    const agent = request.agent(app);
    await getCsrf(agent, '/signin');
    const res = await agent.post('/demo/login').type('form').send({ _csrf: 'nope', role: 'Admin' });
    expect(res.status).toBe(403);
  });
});

describe('signed-in layout and role checks', () => {
  it('Admin sees Settings in the nav and can open /settings', async () => {
    const agent = await signedIn('Admin');
    const home = await agent.get('/');
    expect(home.status).toBe(200);
    expect(home.text).toContain('href="/settings/general"');
    expect(home.text).toContain('Skip to main content');
    expect(home.text).toContain('aria-label="Main"');
    expect((await agent.get('/settings/general')).status).toBe(200);
  });

  it.each(['Manager', 'User'] as const)('%s gets no Settings link and a 403 page on /settings', async (role) => {
    const agent = await signedIn(role);
    const home = await agent.get('/');
    expect(home.text).not.toContain('/settings');
    const res = await agent.get('/settings/general');
    expect(res.status).toBe(403);
    expect(res.text).toContain("You don&#39;t have access to this page.");
  });

  it('the dashboard marks the current nav item', async () => {
    const agent = await signedIn('User');
    expect((await agent.get('/')).text).toContain('aria-current="page"');
  });

  it('sign-out destroys the session', async () => {
    const agent = await signedIn('User');
    const csrf = await getCsrf(agent, '/');
    const out = await agent.post('/auth/signout').type('form').send({ _csrf: csrf });
    expect(out.status).toBe(303);
    expect(out.headers.location).toBe('/signed-out');
    expect((await agent.get('/')).status).toBe(303);
  });

  it('shows the 404 page for unknown routes when signed in', async () => {
    const agent = await signedIn('User');
    const res = await agent.get('/nope-nothing');
    expect(res.status).toBe(404);
    expect(res.text).toContain("We can&#39;t find that page or record.");
  });
});

describe('/__test/login (NODE_ENV=test only)', () => {
  it('signs in as the chosen role', async () => {
    const { app } = makeApp({ config: demoConfig() });
    const agent = request.agent(app);
    await agent.get('/__test/login?role=Admin');
    expect((await agent.get('/settings/general')).status).toBe(200);
  });
  it('is not mounted in development demo mode', async () => {
    const { app } = makeApp({ config: demoConfig({ NODE_ENV: 'development', DEMO_MODE: 'true' }) });
    expect((await request(app).get('/__test/login?role=Admin')).status).toBe(303); // falls through to the auth gate
  });
});
