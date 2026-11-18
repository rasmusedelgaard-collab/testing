import { Router } from 'express';

/** Placeholder pages for sections built in later increments, so the navigation has no dead links. */
const SECTIONS: Array<{ path: string; title: string; nav: string }> = [
  { path: '/analytics', title: 'Analytics', nav: 'analytics' },
  { path: '/search', title: 'Search', nav: '' },
];

export function createAppRouter(): Router {
  const router = Router();

  router.get('/', (req, res) => {
    const first = (req.session.user?.name ?? '').split(' ')[0] || 'there';
    res.render('pages/dashboard', { title: 'Dashboard', activeNav: 'dashboard', firstName: first });
  });

  for (const s of SECTIONS) {
    router.get(s.path, (_req, res) => res.render('pages/coming-soon', { title: s.title, activeNav: s.nav }));
  }


  return router;
}
