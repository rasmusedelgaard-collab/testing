import { Router, type Request, type Response } from 'express';
import { safeReturnTo } from '../auth/returnTo';
import { ENTITIES, ENTITY_LIST, parseListQuery, type EntityDef } from '../domain/entities';
import type { CrmStore, EntityKey, Lookups, OwnerOption, Repo, Row, Values } from '../domain/types';
import { validateEntity, type Errors } from '../domain/validators';
import { ConflictError, EditConflictError, ForbiddenError, NotFoundError } from '../errors';
import { makeFormatter, todayIn } from '../services/format';
import { canDelete, cellsFor, columnsFor, detailRows, errorList, filterFields, formFields, formValuesFromRow, pageHref, recordName, relatedTo, TYPE_LABEL, type FormEnv } from './crmViews';

const REF_FIELDS: Array<[string, EntityKey, string]> = [
  ['AccountId', 'accounts', 'Choose an account from the list.'],
  ['PrimaryContactId', 'contacts', 'Choose a contact from the list.'],
  ['ContactId', 'contacts', 'Choose a contact from the list.'],
  ['OpportunityId', 'opportunities', 'Choose an opportunity from the list.'],
];
const REF_NEEDED: Record<EntityKey, EntityKey[]> = { accounts: [], contacts: ['accounts'], opportunities: ['accounts', 'contacts'], activities: ['accounts', 'contacts', 'opportunities'] };
const FK_MESSAGE: Record<EntityKey, string> = {
  accounts: "This account still has linked opportunities or records you can't see. Move or delete them first.",
  contacts: "This contact is still linked to records you can't see. Move or delete them first.",
  opportunities: "This opportunity is still linked to records you can't see. Move or delete them first.",
  activities: 'This activity cannot be deleted right now.',
};

const parseId = (raw: string | string[] | undefined): number => {
  if (typeof raw !== 'string' || !/^[1-9]\d{0,9}$/.test(raw)) throw new NotFoundError();
  const n = Number(raw);
  if (n > 2_147_483_647) throw new NotFoundError();
  return n;
};
const str = (v: unknown) => (typeof v === 'string' ? v : '');
const cap = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);

/** CRUD routes for the four entities. Every database access is one store.run (one RLS transaction) per request. */
export function createCrmRouter(store: CrmStore): Router {
  const router = Router();
  for (const def of ENTITY_LIST) mount(router, store, def);
  return router;
}

function mount(router: Router, store: CrmStore, def: EntityDef): void {
  const key = def.key;
  const base = def.path;
  const ctxOf = (req: Request) => {
    if (!req.rls) throw new ForbiddenError();
    return req.rls;
  };
  const metaOf = (req: Request) => ({ userId: ctxOf(req).userId, correlationId: req.id });
  const flash = (req: Request, message: string, kind: 'success' | 'error' = 'success') => { req.session.flash = { kind, message }; };

  async function formEnv(repo: Repo, req: Request, lookups: Lookups, current: Row | null): Promise<FormEnv> {
    const need = REF_NEEDED[key];
    const refs = { accounts: [] as never[], contacts: [] as never[], opportunities: [] as never[] } as FormEnv['refs'];
    for (const e of need) (refs as Record<string, unknown>)[e] = await repo.refOptions(e);
    const owners = await repo.owners();
    const currentOwnerId = current ? Number(current.OwnerId) : null;
    const currentOwner = currentOwnerId ? await repo.ownerById(currentOwnerId) : null;
    return { lookups, owners, refs, role: ctxOf(req).role, currentOwner, currentOwnerId };
  }

  const renderForm = (res: Response, req: Request, p: { title: string; action: string; cancel: string; values: Record<string, string>; errors: Errors; env: FormEnv; rowVer?: string }) => {
    const fields = formFields(key, p.values, p.errors, p.env);
    res.status(Object.keys(p.errors).length ? 422 : 200).render('pages/entity-form', {
      title: p.title, activeNav: key, def, fields, errors: errorList(fields, p.errors), action: p.action, cancel: p.cancel, rowVer: p.rowVer ?? '',
    });
  };

  // ---------- list ----------
  router.get(base, async (req, res) => {
    const out = await store.run(ctxOf(req), async (repo) => {
      const lk = await repo.lookups();
      const today = todayIn(lk.settings.timezone);
      const q = parseListQuery(def, req.query as Record<string, unknown>, today);
      const [result, owners] = await Promise.all([repo.list(key, q), repo.owners()]);
      return { lk, q, ...result, owners, today };
    });
    const f = makeFormatter(out.lk.settings);
    const returnTo = pageHref(base, out.q, {});
    const pageCount = Math.max(1, Math.ceil(out.total / out.q.pageSize));
    const isFiltered = Boolean(out.q.q) || Object.entries(out.q.filters).some(([k, v]) => def.filters.find((x) => x.key === k)?.default !== v);
    res.render('pages/entity-list', {
      title: def.plural, activeNav: key, def, total: out.total, q: out.q, isFiltered,
      columns: columnsFor(key).map((c) => ({ label: c.label, href: c.sort ? pageHref(base, out.q, { sort: c.sort, dir: out.q.sort === c.sort && out.q.dir === 'asc' ? 'desc' : 'asc', page: 1 }) : null, aria: out.q.sort === c.sort ? (out.q.dir === 'asc' ? 'ascending' : 'descending') : 'none' })),
      rows: out.rows.map((r) => ({ cells: cellsFor(key, r, f, out.today, returnTo) })),
      filters: filterFields(key, out.q, out.lk, out.owners, ctxOf(req).role === 'Admin'),
      page: out.q.page, pageCount, prevHref: out.q.page > 1 ? pageHref(base, out.q, { page: out.q.page - 1 }) : null, nextHref: out.q.page < pageCount ? pageHref(base, out.q, { page: out.q.page + 1 }) : null,
      from: out.total === 0 ? 0 : (out.q.page - 1) * out.q.pageSize + 1, to: Math.min(out.total, out.q.page * out.q.pageSize), clearHref: base, pageSizes: [25, 50, 100],
    });
  });

  // ---------- new ----------
  router.get(`${base}/new`, async (req, res) => {
    const out = await store.run(ctxOf(req), async (repo) => {
      const lk = await repo.lookups();
      return { lk, env: await formEnv(repo, req, lk, null) };
    });
    const values: Record<string, string> = { OwnerId: String(ctxOf(req).userId), CurrencyCode: out.lk.settings.baseCurrency, ActivityType: 'task' };
    if (key === 'opportunities') values.StageId = String(out.lk.stages.find((s) => s.type === 'open')?.id ?? '');
    for (const [q, field] of [['accountId', 'AccountId'], ['contactId', 'ContactId'], ['opportunityId', 'OpportunityId']] as const) {
      const v = str(req.query[q]);
      if (/^[1-9]\d{0,9}$/.test(v) && def.columns.some((c) => c.name === field)) values[field] = v;
    }
    renderForm(res, req, { title: `New ${def.singular}`, action: base, cancel: base, values, errors: {}, env: out.env });
  });

  // ---------- create ----------
  router.post(base, async (req, res) => {
    const body = req.body as Record<string, unknown>;
    const out = await store.run(ctxOf(req), async (repo) => {
      const lk = await repo.lookups();
      const env = await formEnv(repo, req, lk, null);
      const { values, errors } = await prepare(repo, req, lk, env, body, null);
      if (Object.keys(errors).length > 0) return { kind: 'invalid' as const, env, errors };
      const id = await repo.create(key, values, metaOf(req));
      const visible = await repo.get(key, id);
      const owner = await repo.ownerById(Number(values.OwnerId));
      return { kind: 'saved' as const, id, visible: visible !== null, ownerName: owner?.name ?? '' };
    });
    if (out.kind === 'invalid') return renderForm(res, req, { title: `New ${def.singular}`, action: base, cancel: base, values: rawValues(body), errors: out.errors, env: out.env });
    afterSave(req, res, out);
  });

  const afterSave = (req: Request, res: Response, out: { id: number; visible: boolean; ownerName: string }) => {
    if (out.visible) {
      flash(req, `${cap(def.singular)} saved.`);
      return res.redirect(303, `${base}/${out.id}`);
    }
    flash(req, `Saved and assigned to ${out.ownerName}.`);
    res.redirect(303, base);
  };

  /** Validates input, owner choice and referenced records (all visible under RLS) inside the same transaction. */
  async function prepare(repo: Repo, req: Request, lk: Lookups, env: FormEnv, body: Record<string, unknown>, current: Row | null): Promise<{ values: Values; errors: Errors }> {
    const { values, errors } = validateEntity(key, body, lk);
    const ownerId = Number(str(body.OwnerId));
    if (!errors.OwnerId) {
      const allowed = env.owners.some((o: OwnerOption) => o.id === ownerId) || (current !== null && Number(current.OwnerId) === ownerId);
      if (!allowed) errors.OwnerId = ctxOf(req).role === 'Admin' ? 'Choose an owner.' : 'Choose an owner from your team.';
    }
    {
      for (const [field, entity, message] of REF_FIELDS) {
        const id = values[field];
        if (typeof id === 'number' && !errors[field] && !(await repo.refExists(entity, id))) errors[field] = message;
      }
    }
    return { values, errors };
  }

  const rawValues = (body: Record<string, unknown>): Record<string, string> => Object.fromEntries(Object.entries(body).map(([k, v]) => [k, str(v)]));

  // ---------- detail ----------
  router.get(`${base}/:id`, async (req, res) => {
    const id = parseId(req.params.id);
    const tabParam = str(req.query.tab);
    const out = await store.run(ctxOf(req), async (repo) => {
      const row = await repo.get(key, id);
      if (!row) return null;
      const lk = await repo.lookups();
      const today = todayIn(lk.settings.timezone);
      const rel = async (entity: EntityKey, filter: string) => (await repo.list(entity, { q: '', page: 1, pageSize: 100, sort: ENTITIES[entity].defaultSort, dir: 'asc', today, filters: { [filter]: String(id), ...(entity === 'opportunities' ? { status: 'all' } : {}), ...(entity === 'activities' ? { status: 'all' } : {}) } })).rows;
      const related: Partial<Record<EntityKey, Row[]>> = {};
      if (key === 'accounts') { related.contacts = await rel('contacts', 'account'); related.opportunities = await rel('opportunities', 'account'); related.activities = await rel('activities', 'account'); }
      if (key === 'contacts') { related.opportunities = await rel('opportunities', 'contact'); related.activities = await rel('activities', 'contact'); }
      if (key === 'opportunities') related.activities = await rel('activities', 'opportunity');
      return { row, lk, today, related };
    });
    if (!out) throw new NotFoundError();
    const f = makeFormatter(out.lk.settings);
    const user = ctxOf(req);
    const tabs = (Object.keys(out.related) as EntityKey[]).map((e) => ({ key: e, label: ENTITIES[e].plural, count: out.related[e]!.length }));
    const active = tabs.find((t) => t.key === tabParam) ?? tabs[0];
    const detailPath = `${base}/${id}`;
    const tabTable = active ? {
      entity: active.key, title: active.label,
      columns: columnsFor(active.key).map((c) => c.label),
      rows: out.related[active.key]!.map((r) => ({ cells: cellsFor(active.key, r, f, out.today, detailPath) })),
      newHref: `${ENTITIES[active.key].path}/new?${key === 'accounts' ? 'accountId' : key === 'contacts' ? 'contactId' : 'opportunityId'}=${id}`,
      newLabel: active.key === 'activities' ? 'Log activity' : `New ${ENTITIES[active.key].singular}`,
    } : null;
    const row = out.row;
    res.render('pages/entity-detail', {
      title: recordName(key, row), activeNav: key, def, row, name: recordName(key, row), details: detailRows(key, row, f), canDelete: canDelete(user, row),
      tabs: tabs.map((t) => ({ ...t, href: `${detailPath}?tab=${t.key}`, active: t.key === active?.key })), tabTable,
      modified: { by: String(row.ModifiedByName ?? ''), at: f.dateTime(String(row.ModifiedAt)), createdBy: String(row.CreatedByName ?? ''), createdAt: f.dateTime(String(row.CreatedAt)), edited: row.ModifiedAt !== row.CreatedAt || row.ModifiedBy !== row.CreatedBy },
      stageActions: key === 'opportunities' && row.StageType === 'open' ? { rowVer: String(row.RowVer), lostReasons: (out.lk.picklists.lost_reason ?? []).filter((p) => p.active) } : null,
      doneAction: key === 'activities' ? { action: `${detailPath}/${row.IsDone ? 'undone' : 'done'}`, label: row.IsDone ? 'Mark as not done' : 'Mark as done', returnTo: detailPath } : null,
      related: key === 'activities' ? relatedTo(row) : null, typeLabel: TYPE_LABEL[String(row.ActivityType)] ?? '',
    });
  });

  // ---------- edit ----------
  router.get(`${base}/:id/edit`, async (req, res) => {
    const id = parseId(req.params.id);
    const out = await store.run(ctxOf(req), async (repo) => {
      const row = await repo.get(key, id);
      if (!row) return null;
      const lk = await repo.lookups();
      return { row, lk, env: await formEnv(repo, req, lk, row) };
    });
    if (!out) throw new NotFoundError();
    const f = makeFormatter(out.lk.settings);
    renderForm(res, req, { title: `Edit ${recordName(key, out.row)}`, action: `${base}/${id}`, cancel: `${base}/${id}`, values: formValuesFromRow(key, out.row, f), errors: {}, env: out.env, rowVer: String(out.row.RowVer) });
  });

  // ---------- update ----------
  router.post(`${base}/:id`, async (req, res) => {
    const id = parseId(req.params.id);
    const body = req.body as Record<string, unknown>;
    const rowVer = str(body.rowVer);
    const out = await store.run(ctxOf(req), async (repo) => {
      const current = await repo.get(key, id);
      if (!current) return { kind: 'notfound' as const };
      const lk = await repo.lookups();
      const env = await formEnv(repo, req, lk, current);
      const { values, errors } = await prepare(repo, req, lk, env, body, current);
      if (Object.keys(errors).length > 0) return { kind: 'invalid' as const, env, errors, name: recordName(key, current) };
      const result = await repo.update(key, id, values, rowVer, metaOf(req));
      if (result !== 'ok') return { kind: result };
      const visible = await repo.get(key, id);
      const owner = await repo.ownerById(Number(values.OwnerId));
      return { kind: 'saved' as const, visible: visible !== null, ownerName: owner?.name ?? '' };
    });
    if (out.kind === 'notfound') throw new NotFoundError();
    if (out.kind === 'conflict') throw new EditConflictError(`${base}/${id}`);
    if (out.kind === 'invalid') return renderForm(res, req, { title: `Edit ${out.name}`, action: `${base}/${id}`, cancel: `${base}/${id}`, values: rawValues(body), errors: out.errors, env: out.env, rowVer });
    if (out.kind !== 'saved') throw new NotFoundError();
    afterSave(req, res, { id, visible: out.visible, ownerName: out.ownerName });
  });

  // ---------- delete ----------
  router.get(`${base}/:id/delete`, async (req, res) => {
    const id = parseId(req.params.id);
    const row = await store.run(ctxOf(req), (repo) => repo.get(key, id));
    if (!row) throw new NotFoundError();
    if (!canDelete(ctxOf(req), row)) throw new ForbiddenError();
    res.render('pages/entity-delete', { title: `Delete ${recordName(key, row)}`, activeNav: key, def, name: recordName(key, row), id, notice: deleteNotice(key) });
  });

  router.post(`${base}/:id/delete`, async (req, res) => {
    const id = parseId(req.params.id);
    try {
      const done = await store.run(ctxOf(req), async (repo) => {
        const row = await repo.get(key, id);
        if (!row) return false;
        return repo.remove(key, id, metaOf(req));
      });
      if (!done) throw new NotFoundError();
    } catch (err) {
      if (err instanceof ConflictError && !(err instanceof EditConflictError)) throw new ConflictError(FK_MESSAGE[key], { cause: err });
      throw err;
    }
    flash(req, `${cap(def.singular)} deleted.`);
    res.redirect(303, base);
  });

  // ---------- opportunity: won / lost ----------
  if (key === 'opportunities') {
    for (const outcome of ['won', 'lost'] as const) {
      router.post(`${base}/:id/${outcome}`, async (req, res) => {
        const id = parseId(req.params.id);
        const body = req.body as Record<string, unknown>;
        const out = await store.run(ctxOf(req), async (repo) => {
          const row = await repo.get(key, id);
          if (!row) return { kind: 'notfound' as const };
          const lk = await repo.lookups();
          const stage = lk.stages.find((s) => s.type === outcome);
          if (!stage) return { kind: 'error' as const, message: `No ${outcome} stage is configured.` };
          const lostReason = Number(str(body.lostReason));
          if (outcome === 'lost' && !(lk.picklists.lost_reason ?? []).some((p) => p.active && p.id === lostReason)) return { kind: 'error' as const, message: 'Choose why this was lost.' };
          const values: Values = Object.fromEntries(def.columns.map((c) => [c.name, row[c.name] ?? null]));
          const today = todayIn(lk.settings.timezone);
          Object.assign(values, { StageId: stage.id, LostReasonValueId: outcome === 'lost' ? lostReason : null, OwnerId: row.OwnerId });
          const moved = String(row.CloseDate) > today;
          if (moved) values.CloseDate = today;
          const result = await repo.update(key, id, values, str(body.rowVer) || String(row.RowVer), metaOf(req));
          return result === 'ok' ? { kind: 'ok' as const, moved } : { kind: result };
        });
        if (out.kind === 'notfound') throw new NotFoundError();
        if (out.kind === 'conflict') throw new EditConflictError(`${base}/${id}`);
        if (out.kind === 'error') flash(req, out.message, 'error');
        else flash(req, `Opportunity marked as ${outcome}.${out.moved ? ' Close date set to today.' : ''}`);
        res.redirect(303, `${base}/${id}`);
      });
    }
  }

  // ---------- activity: done / undone ----------
  if (key === 'activities') {
    for (const done of [true, false]) {
      router.post(`${base}/:id/${done ? 'done' : 'undone'}`, async (req, res) => {
        const id = parseId(req.params.id);
        const ok = await store.run(ctxOf(req), (repo) => repo.setDone(id, done, metaOf(req)));
        if (!ok) throw new NotFoundError();
        flash(req, done ? 'Marked as done.' : 'Marked as not done.');
        res.redirect(303, safeReturnTo((req.body as Record<string, unknown>).returnTo, base));
      });
    }
  }
}

function deleteNotice(key: EntityKey): string {
  switch (key) {
    case 'accounts': return "This also removes the link between this account and its contacts and activities. The records themselves are kept.";
    case 'contacts': return 'This also removes the link between this contact and its opportunities and activities.';
    case 'opportunities': return 'This also removes the link between this opportunity and its activities.';
    case 'activities': return '';
  }
}
