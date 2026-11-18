import { Router } from 'express';
import { establishSession } from './establishSession';
import { safeReturnTo } from './returnTo';
import type { Role, SessionUser } from './types';

/**
 * No-DB demo/test sign-in for local UI work and e2e (NODE_ENV=test or DEMO_MODE=true). Config refuses
 * to start with DEMO_MODE in production or on App Service, so this router is never mounted there.
 */
const DEMO_USERS: Record<Role, SessionUser> = {
  Admin: { userId: 1, oid: '00000000-0000-4000-8000-0000000d0001', name: 'Demo Admin', email: 'admin@demo.invalid', role: 'Admin', teamId: 1, authAt: 0 },
  Manager: { userId: 2, oid: '00000000-0000-4000-8000-0000000d0002', name: 'Demo Manager', email: 'manager@demo.invalid', role: 'Manager', teamId: 1, authAt: 0 },
  User: { userId: 3, oid: '00000000-0000-4000-8000-0000000d0003', name: 'Demo User', email: 'user@demo.invalid', role: 'User', teamId: 1, authAt: 0 },
};

export function isRole(v: unknown): v is Role {
  return v === 'Admin' || v === 'Manager' || v === 'User';
}

export function demoUser(role: Role, now = Date.now()): SessionUser {
  return { ...DEMO_USERS[role], authAt: now };
}

export function createDemoAuthRouter(opts: { allowGetLogin: boolean }): Router {
  const router = Router();
  router.post('/demo/login', async (req, res) => {
    const role = req.body?.role;
    if (!isRole(role)) return res.status(400).render('pages/error', { title: 'Bad request', heading: 'Choose a role', message: 'Pick Admin, Manager or User.', reference: req.id, status: 400 });
    await establishSession(req, demoUser(role));
    res.redirect(303, safeReturnTo(req.body?.returnTo));
  });
  if (opts.allowGetLogin) {
    // Test automation convenience (NODE_ENV=test only): GET /__test/login?role=Admin
    router.get('/__test/login', async (req, res) => {
      const role = req.query.role ?? 'User';
      if (!isRole(role)) return res.status(400).send('bad role');
      await establishSession(req, demoUser(role));
      res.redirect(303, safeReturnTo(req.query.returnTo));
    });
  }
  return router;
}
