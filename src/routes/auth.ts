import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import { Router } from 'express';
import type { Request } from 'express';
import { destroySession, establishSession } from '../auth/establishSession';
import { mapRole } from '../auth/roles';
import { safeReturnTo } from '../auth/returnTo';
import type { OidcClient } from '../auth/msal';
import { syncUser } from '../auth/userSync';
import type { AppConfig } from '../config';
import type { UserRepo } from '../db/userRepo';
import { logger } from '../logger';

const b64url = (buf: Buffer) => buf.toString('base64url');

function equalSecrets(a: string, b: string): boolean {
  const x = Buffer.from(a);
  const y = Buffer.from(b);
  return x.length === y.length && timingSafeEqual(x, y);
}

const saveSession = (req: Request) =>
  new Promise<void>((resolve, reject) => req.session.save((e) => (e ? reject(e as Error) : resolve())));

function fail(req: Request, res: import('express').Response, status: number, why: string) {
  logger.warn({ reqId: req.id, why }, 'sign-in failed');
  res.status(status).render('pages/signin-error', { title: 'Sign-in problem', reference: req.id });
}

/** Entra auth-code + PKCE flow (ADR-0003). Mounted only when AUTH_MODE=entra and not in demo mode. */
export function createEntraAuthRouter(deps: { config: AppConfig; oidc: OidcClient; users: UserRepo }): Router {
  const { config, oidc, users } = deps;
  const router = Router();

  router.get('/auth/login', async (req, res) => {
    const verifier = b64url(randomBytes(32));
    const state = b64url(randomBytes(32));
    const nonce = b64url(randomBytes(32));
    const challenge = b64url(createHash('sha256').update(verifier).digest());
    req.session.auth = { state, nonce, pkceVerifier: verifier, returnTo: safeReturnTo(req.query.returnTo) };
    await saveSession(req);
    res.redirect(302, await oidc.authUrl({ state, nonce, codeChallenge: challenge }));
  });

  router.get('/auth/redirect', async (req, res) => {
    const pending = req.session.auth;
    delete req.session.auth; // single use, whatever happens next
    const { code, state, error } = req.query;
    if (!pending) return fail(req, res, 400, 'no pending login');
    if (typeof error === 'string') return fail(req, res, 400, `idp error ${error.slice(0, 40)}`);
    if (typeof code !== 'string' || typeof state !== 'string' || !equalSecrets(state, pending.state)) {
      return fail(req, res, 400, 'state mismatch');
    }

    let claims;
    try {
      claims = await oidc.redeem({ code, codeVerifier: pending.pkceVerifier });
    } catch (err) {
      logger.warn({ reqId: req.id, err: (err as Error).message }, 'code redemption failed');
      return fail(req, res, 502, 'redeem failed');
    }
    if (claims.tid.toLowerCase() !== config.entra.tenantId.toLowerCase()) return fail(req, res, 403, 'tenant mismatch');
    if (!claims.nonce || !equalSecrets(claims.nonce, pending.nonce)) return fail(req, res, 400, 'nonce mismatch');

    const role = mapRole(claims.roles);
    if (!role) {
      logger.info({ oid: claims.oid }, 'sign-in without a CRM app role');
      await saveSession(req);
      return res.redirect(303, '/no-access');
    }

    const user = await syncUser(users, { claims, role, tenantId: claims.tid });
    await establishSession(req, user);
    res.redirect(303, pending.returnTo);
  });

  return router;
}

/** Shared by all auth modes. */
export function createCommonAuthRouter(deps: { config: AppConfig; oidc?: OidcClient; loginPath: string }): Router {
  const { config, oidc, loginPath } = deps;
  const router = Router();

  router.get('/signin', (req, res) => {
    res.render('pages/signin', {
      title: 'Sign in',
      returnTo: safeReturnTo(req.query.returnTo),
      reason: req.query.reason === 'expired' ? 'expired' : null,
      loginPath,
      demo: config.demoMode,
    });
  });

  router.get('/signed-out', (_req, res) => res.render('pages/signed-out', { title: 'Signed out' }));

  router.get('/no-access', (_req, res) => res.status(403).render('pages/no-access', { title: 'No access' }));

  router.post('/auth/signout', async (req, res) => {
    await destroySession(req);
    res.clearCookie(config.secureCookies ? '__Host-crm.sid' : 'crm.sid', { path: '/' });
    res.redirect(303, oidc ? oidc.logoutUrl() : '/signed-out');
  });

  return router;
}
