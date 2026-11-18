import { Router, type Request } from 'express';
import type { CrmStore, ListQuery, Row } from '../domain/types';
import { ForbiddenError } from '../errors';
import { activitiesByType, oppsByLeadSource, pipelineByStage, winRateByOwner, wonRevenueByMonth, type ChartVM, type FormatCtx } from '../services/charts';
import { makeConverter } from '../services/fx';
import { makeFormatter, todayIn } from '../services/format';
import { computeTiles, winRateRange, type Tile } from '../services/kpi';
import { monthOf, PERIOD_LABEL, PERIODS, quarterRange, resolvePeriod } from '../services/periods';
import { cellsFor, columnsFor } from './crmViews';

const str = (v: unknown) => (typeof v === 'string' ? v : '');

function greeting(tz: string, now = new Date()): string {
  const h = Number(new Intl.DateTimeFormat('en-GB', { timeZone: tz, hour: '2-digit', hourCycle: 'h23' }).format(now));
  return h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
}

const listQuery = (today: string, filters: Record<string, string>, sort = 'due'): ListQuery => ({ q: '', page: 1, pageSize: 10, sort, dir: 'asc', today, filters });

/** Dashboard and Analytics. All data comes from one store.run (one RLS transaction) per request, so RLS filters every number. */
export function createDashboardRouter(store: CrmStore): Router {
  const router = Router();
  const ctxOf = (req: Request) => {
    if (!req.rls) throw new ForbiddenError();
    return req.rls;
  };

  router.get('/', async (req, res) => {
    const ctx = ctxOf(req);
    const out = await store.run(ctx, async (repo) => {
      const lk = await repo.lookups();
      const s = lk.settings;
      const today = todayIn(s.timezone);
      const quarter = quarterRange(today, s.fiscalStartMonth);
      const win = winRateRange(today);
      // Sequential on purpose: one transaction = one connection = one request at a time (no Promise.all here).
      const rates = await repo.fxRates(s.baseCurrency);
      const openRows = await repo.oppAggregates({ types: ['open'], groupBy: ['stage'] });
      const quarterWonRows = await repo.oppAggregates({ types: ['won'], from: quarter.from, to: quarter.to, groupBy: ['month'] });
      const winRateRows = await repo.oppAggregates({ types: ['won', 'lost'], from: win.from, to: win.to, groupBy: [] });
      const counts = await repo.activityCounts(today);
      const mine = await repo.list('opportunities', listQuery(today, { status: 'open', owner: String(ctx.userId) }, 'close'));
      const actFilters = { owner: String(ctx.userId), status: 'open', type: 'actionable' };
      const upcoming = {
        overdue: await repo.list('activities', listQuery(today, { ...actFilters, due: 'overdue' })),
        today: await repo.list('activities', listQuery(today, { ...actFilters, due: 'today' })),
        week: await repo.list('activities', listQuery(today, { ...actFilters, due: 'week' })),
      };
      return { lk, today, tiles: computeTiles({ today, fiscalStartMonth: s.fiscalStartMonth, stages: lk.stages, conv: makeConverter(s.baseCurrency, rates), openRows, quarterWonRows, winRateRows }), counts, mine, upcoming };
    });
    const s = out.lk.settings;
    const f = makeFormatter(s);
    const compact = new Intl.NumberFormat(s.localeCode, { style: 'currency', currency: s.baseCurrency, notation: 'compact', minimumFractionDigits: 0, maximumFractionDigits: 1 });
    const percent = new Intl.NumberFormat(s.localeCode, { style: 'percent', maximumFractionDigits: 0 });
    const tile = (t: Tile) => ({
      label: t.label, href: t.href, context: t.context, notes: t.notes, excluded: t.notes.some((n) => n.includes('excluded')),
      short: t.value === null ? '–' : t.kind === 'money' ? compact.format(t.value) : percent.format(t.value / 100),
      full: t.value === null ? 'No closed deals yet' : t.kind === 'money' ? f.money(t.value, s.baseCurrency) : '',
      danger: false,
    });
    const tiles = out.tiles.map(tile);
    tiles.push({ label: 'Activities due', href: '/activities?status=open&type=actionable&due=due', short: `${out.counts.today} due today`, full: '', context: out.counts.overdue > 0 ? `${out.counts.overdue} overdue` : 'Nothing overdue', notes: [], excluded: false, danger: out.counts.overdue > 0 });
    const rows = (rs: Row[], entity: 'opportunities' | 'activities') => rs.map((r) => ({ cells: cellsFor(entity, r, f, out.today, '/') }));
    res.render('pages/dashboard', {
      title: 'Dashboard', activeNav: 'dashboard',
      greeting: `${greeting(s.timezone)}, ${(req.session.user?.name ?? '').split(' ')[0] || 'there'}`, asOf: f.dateTime(new Date().toISOString()),
      tiles, isAdmin: ctx.role === 'Admin',
      oppColumns: columnsFor('opportunities').map((c) => c.label), opps: rows(out.mine.rows, 'opportunities'), oppTotal: out.mine.total,
      actColumns: columnsFor('activities').map((c) => c.label),
      upcoming: [
        { title: 'Overdue', total: out.upcoming.overdue.total, rows: rows(out.upcoming.overdue.rows, 'activities'), viewAll: '/activities?status=open&type=actionable&due=overdue' },
        { title: 'Today', total: out.upcoming.today.total, rows: rows(out.upcoming.today.rows, 'activities'), viewAll: '/activities?status=open&type=actionable&due=today' },
        { title: 'Next 7 days', total: out.counts.next7, rows: rows(out.upcoming.week.rows, 'activities'), viewAll: '/activities?status=open&type=actionable&due=week' },
      ],
    });
  });

  router.get('/analytics', async (req, res) => {
    const ctx = ctxOf(req);
    const q = req.query as Record<string, unknown>;
    const ownerRaw = str(q.owner);
    const ownerId = /^[1-9]\d{0,9}$/.test(ownerRaw) && Number(ownerRaw) <= 2_147_483_647 ? Number(ownerRaw) : undefined;
    const out = await store.run(ctx, async (repo) => {
      const lk = await repo.lookups();
      const s = lk.settings;
      const today = todayIn(s.timezone);
      const period = resolvePeriod({ period: str(q.period), from: str(q.from), to: str(q.to) }, today, s.fiscalStartMonth);
      const r = period.range;
      const rates = await repo.fxRates(s.baseCurrency);
      const openRows = await repo.oppAggregates({ types: ['open'], from: r.from, to: r.to, ownerId, groupBy: ['stage'] });
      const closedRows = await repo.oppAggregates({ types: ['won', 'lost'], from: r.from, to: r.to, ownerId, groupBy: ['owner', 'month'] });
      const allRows = await repo.oppAggregates({ types: ['open', 'won', 'lost'], from: r.from, to: r.to, ownerId, groupBy: ['leadSource'] });
      const acts = await repo.activitiesByType(r, ownerId);
      const owners = await repo.owners();
      return { lk, today, period, rates, openRows, closedRows, allRows, acts, owners };
    });
    const s = out.lk.settings;
    const f = makeFormatter(s);
    const c: FormatCtx = { f, locale: s.localeCode, base: s.baseCurrency };
    const conv = makeConverter(s.baseCurrency, out.rates);
    const wonRows = out.closedRows.filter((x) => x.stageType === 'won');
    const charts: ChartVM[] = [
      pipelineByStage(out.openRows, out.lk.stages, conv, monthOf(out.today), c),
      wonRevenueByMonth(wonRows, out.period.range, conv, c),
      winRateByOwner(out.closedRows, c),
      activitiesByType(out.acts, c),
      oppsByLeadSource(out.allRows, c),
    ];
    const missing = charts.some((ch) => ch.notes.some((n) => n.includes('excluded')));
    res.render('pages/analytics', {
      title: 'Analytics', activeNav: 'analytics', scripts: ['/vendor/chart.umd.js', '/js/charts.js'],
      period: out.period.key, error: out.period.ok ? '' : out.period.error, from: out.period.ok && out.period.key !== 'custom' ? out.period.range.from : str(q.from), to: out.period.ok && out.period.key !== 'custom' ? out.period.range.to : str(q.to),
      rangeText: `${f.date(out.period.range.from)} – ${f.date(out.period.range.to)}`,
      periods: PERIODS.map((k) => ({ value: k, label: PERIOD_LABEL[k] })), ownerId: ownerId ?? 0,
      owners: out.owners.map((o) => ({ id: o.id, label: ctx.role === 'Admin' && o.teamName ? `${o.name} (${o.teamName})` : o.name })),
      charts: charts.map((ch) => ({ ...ch, json: JSON.stringify({ kind: ch.kind, labels: ch.labels, data: ch.data, valueKind: ch.valueKind, seriesLabel: ch.seriesLabel, locale: s.localeCode, currency: s.baseCurrency }).replace(/</g, '\\u003c') })),
      fxMissing: missing, isAdmin: ctx.role === 'Admin',
    });
  });

  return router;
}
