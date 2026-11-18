import { createHash } from 'node:crypto';
import { Router } from 'express';
import { darken } from '../services/color';
import { rateLimit } from './rateLimit';
import type { BrandingProvider } from '../services/brandingTypes';

/** Public: theme stylesheet and logo (the sign-in page needs both), plus layout locals on every request. */
export function createBrandingRouter(branding: BrandingProvider): Router {
  const router = Router();

  router.use(async (_req, res, next) => {
    const b = await branding.get();
    res.locals.appName = b.appName;
    res.locals.logoUrl = b.logoSha ? `/branding/logo?v=${b.logoSha.slice(0, 12)}` : null;
    res.locals.themeVersion = createHash('sha256').update(b.accentColor).digest('hex').slice(0, 8);
    next();
  });

  // Accent colour as a stylesheet, because the CSP forbids inline styles. The value is validated (#RRGGBB) on write
  // and re-checked here, so nothing but a hex colour can ever be emitted.
  router.get('/css/theme.css', async (req, res) => {
    const { accentColor } = await branding.get();
    const color = /^#[0-9A-F]{6}$/i.test(accentColor) ? accentColor : '#1D4ED8';
    const css = `:root { --color-primary: ${color}; --color-primary-hover: ${darken(color)}; }\n`;
    const etag = `"${createHash('sha256').update(css).digest('hex').slice(0, 16)}"`;
    res.set({ 'Cache-Control': 'no-cache', ETag: etag, 'Content-Type': 'text/css; charset=utf-8' });
    if (req.get('if-none-match') === etag) return res.status(304).end();
    res.send(css);
  });

  router.get('/branding/logo', rateLimit({ max: 120, windowMs: 60_000 }), async (req, res) => {
    const b = await branding.get();
    if (!b.logoSha) return res.status(404).type('text/plain').send('Not found');
    const etag = `"${b.logoSha}"`;
    res.set({
      ETag: etag,
      'Cache-Control': 'no-cache',
      'X-Content-Type-Options': 'nosniff',
      'Content-Security-Policy': "default-src 'none'; style-src 'unsafe-inline'; sandbox",
    });
    if (req.get('if-none-match') === etag) return res.status(304).end();
    const logo = await branding.logo();
    if (!logo) return res.status(404).type('text/plain').send('Not found');
    res.type(logo.contentType).send(logo.content);
  });

  return router;
}
