import { Router } from 'express';
import type { RequestHandler } from 'express';

/** Placeholder pages for sections built in later increments, so the navigation has no dead links. */
const SECTIONS: Array<{ path: string; title: string; nav: string }> = [
  { path: '/accounts', title: 'Accounts', nav: 'accounts' },
  { path: '/contacts', title: 'Contacts', nav: 'contacts' },
  { path: '/opportunities', title: 'Opportunities', nav: 'opportunities' },
  { path: '/activities', title: 'Activities', nav: 'activities' },
  { path: '/analytics', title: 'Analytics', nav: 'analytics' },
  { path: '/search', title: 'Search', nav: '' },
];

export function createAppRouter(opts: { requireAdmin: RequestHandler }): Router {
  const router = Router();

  router.get('/', (req, res) => {
    const first = (req.session.user?.name ?? '').split(' ')[0] || 'there';
    res.render('pages/dashboard', { title: 'Dashboard', activeNav: 'dashboard', firstName: first });
  });

  for (const s of SECTIONS) {
    router.get(s.path, (_req, res) => res.render('pages/coming-soon', { title: s.title, activeNav: s.nav }));
  }

  // Admin area: route check here, row-level enforcement stays in the database.
  router.get(/^\/settings(\/.*)?$/, opts.requireAdmin, (_req, res) => {
    res.render('pages/coming-soon', { title: 'Settings', activeNav: 'settings' });
  });

  return router;
}
