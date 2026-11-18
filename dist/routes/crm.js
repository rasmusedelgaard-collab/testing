"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.createCrmRouter = createCrmRouter;
const express_1 = require("express");
const returnTo_1 = require("../auth/returnTo");
const entities_1 = require("../domain/entities");
const validators_1 = require("../domain/validators");
const errors_1 = require("../errors");
const format_1 = require("../services/format");
const fx_1 = require("../services/fx");
const crmViews_1 = require("./crmViews");
const REF_FIELDS = [
    ['AccountId', 'accounts', 'Choose an account from the list.'],
    ['PrimaryContactId', 'contacts', 'Choose a contact from the list.'],
    ['ContactId', 'contacts', 'Choose a contact from the list.'],
    ['OpportunityId', 'opportunities', 'Choose an opportunity from the list.'],
];
const REF_NEEDED = { accounts: [], contacts: ['accounts'], opportunities: ['accounts', 'contacts'], activities: ['accounts', 'contacts', 'opportunities'] };
const FK_MESSAGE = {
    accounts: "This account still has linked opportunities or records you can't see. Move or delete them first.",
    contacts: "This contact is still linked to records you can't see. Move or delete them first.",
    opportunities: "This opportunity is still linked to records you can't see. Move or delete them first.",
    activities: 'This activity cannot be deleted right now.',
};
const WRITE_BLOCKED = "You can't save this record with that owner. Choose an owner from your team.";
const parseId = (raw) => {
    if (typeof raw !== 'string' || !/^[1-9]\d{0,9}$/.test(raw))
        throw new errors_1.NotFoundError();
    const n = Number(raw);
    if (n > 2_147_483_647)
        throw new errors_1.NotFoundError();
    return n;
};
const str = (v) => (typeof v === 'string' ? v : '');
const cap = (s) => s.charAt(0).toUpperCase() + s.slice(1);
/** CRUD routes for the four entities. Every database access is one store.run (one RLS transaction) per request. */
function createCrmRouter(store) {
    const router = (0, express_1.Router)();
    for (const def of entities_1.ENTITY_LIST)
        mount(router, store, def);
    return router;
}
function mount(router, store, def) {
    const key = def.key;
    const base = def.path;
    const ctxOf = (req) => {
        if (!req.rls)
            throw new errors_1.ForbiddenError();
        return req.rls;
    };
    const metaOf = (req) => ({ userId: ctxOf(req).userId, correlationId: req.id });
    const flash = (req, message, kind = 'success') => { req.session.flash = { kind, message }; };
    async function formEnv(repo, req, lookups, current) {
        const need = REF_NEEDED[key];
        const refs = { accounts: [], contacts: [], opportunities: [] };
        for (const e of need)
            refs[e] = await repo.refOptions(e);
        const owners = await repo.owners();
        const currentOwnerId = current ? Number(current.OwnerId) : null;
        const currentOwner = currentOwnerId ? await repo.ownerById(currentOwnerId) : null;
        return { lookups, owners, refs, role: ctxOf(req).role, currentOwner, currentOwnerId };
    }
    const renderForm = (res, req, p) => {
        const fields = (0, crmViews_1.formFields)(key, p.values, p.errors, p.env);
        res.status(Object.keys(p.errors).length ? 422 : 200).render('pages/entity-form', {
            title: p.title, activeNav: key, def, fields, errors: (0, crmViews_1.errorList)(fields, p.errors), action: p.action, cancel: p.cancel, rowVer: p.rowVer ?? '',
        });
    };
    // ---------- list ----------
    router.get(base, async (req, res) => {
        const out = await store.run(ctxOf(req), async (repo) => {
            const lk = await repo.lookups();
            const today = (0, format_1.todayIn)(lk.settings.timezone);
            const q = (0, entities_1.parseListQuery)(def, req.query, today);
            // Sequential on purpose: one transaction = one connection = one request at a time.
            const result = await repo.list(key, q);
            const owners = await repo.owners();
            if (key === 'accounts' && result.rows.length > 0) {
                // Open pipeline per account in the base currency, at the current month's rate (ADR-0005); unconvertible deals are called out.
                const aggs = await repo.oppAggregates({ types: ['open'], accountIds: result.rows.map((r) => Number(r.Id)), groupBy: ['account'] });
                const rates = await repo.fxRates(lk.settings.baseCurrency);
                const conv = (0, fx_1.makeConverter)(lk.settings.baseCurrency, rates);
                const fmt = (0, format_1.makeFormatter)(lk.settings);
                for (const r of result.rows) {
                    const mine = aggs.filter((a) => a.accountId === Number(r.Id));
                    if (mine.length === 0)
                        continue;
                    const c = (0, fx_1.convertRows)(mine, conv, () => today.slice(0, 7));
                    const n = c.excluded.reduce((x, e) => x + e.count, 0);
                    const note = n ? `${n} ${n === 1 ? 'opportunity' : 'opportunities'} not converted (no rate: ${c.excluded.map((e) => e.currency).join(', ')})` : '';
                    r.OpenPipeline = `${c.total > 0 || !note ? fmt.money(c.total, lk.settings.baseCurrency) : ''}${note ? `${c.total > 0 ? ' + ' : ''}${note}` : ''}`;
                }
            }
            return { lk, q, ...result, owners, today };
        });
        const f = (0, format_1.makeFormatter)(out.lk.settings);
        const returnTo = (0, crmViews_1.pageHref)(base, out.q, {});
        const pageCount = Math.max(1, Math.ceil(out.total / out.q.pageSize));
        const isFiltered = Boolean(out.q.q) || Object.entries(out.q.filters).some(([k, v]) => def.filters.find((x) => x.key === k)?.default !== v);
        res.render('pages/entity-list', {
            title: def.plural, activeNav: key, def, total: out.total, q: out.q, isFiltered,
            columns: (0, crmViews_1.columnsFor)(key).map((c) => ({ label: c.label, href: c.sort ? (0, crmViews_1.pageHref)(base, out.q, { sort: c.sort, dir: out.q.sort === c.sort && out.q.dir === 'asc' ? 'desc' : 'asc', page: 1 }) : null, aria: out.q.sort === c.sort ? (out.q.dir === 'asc' ? 'ascending' : 'descending') : 'none' })),
            rows: out.rows.map((r) => ({ cells: (0, crmViews_1.cellsFor)(key, r, f, out.today, returnTo) })),
            filters: (0, crmViews_1.filterFields)(key, out.q, out.lk, out.owners, ctxOf(req).role === 'Admin'),
            page: out.q.page, pageCount, prevHref: out.q.page > 1 ? (0, crmViews_1.pageHref)(base, out.q, { page: out.q.page - 1 }) : null, nextHref: out.q.page < pageCount ? (0, crmViews_1.pageHref)(base, out.q, { page: out.q.page + 1 }) : null,
            from: out.total === 0 ? 0 : (out.q.page - 1) * out.q.pageSize + 1, to: Math.min(out.total, out.q.page * out.q.pageSize), clearHref: base, pageSizes: [25, 50, 100],
        });
    });
    // ---------- new ----------
    router.get(`${base}/new`, async (req, res) => {
        const out = await store.run(ctxOf(req), async (repo) => {
            const lk = await repo.lookups();
            return { lk, env: await formEnv(repo, req, lk, null) };
        });
        const values = { OwnerId: String(ctxOf(req).userId), CurrencyCode: out.lk.settings.baseCurrency, ActivityType: 'task' };
        if (key === 'opportunities')
            values.StageId = String(out.lk.stages.find((s) => s.type === 'open')?.id ?? '');
        for (const [q, field] of [['accountId', 'AccountId'], ['contactId', 'ContactId'], ['opportunityId', 'OpportunityId']]) {
            const v = str(req.query[q]);
            if (/^[1-9]\d{0,9}$/.test(v) && def.columns.some((c) => c.name === field))
                values[field] = v;
        }
        renderForm(res, req, { title: `New ${def.singular}`, action: base, cancel: base, values, errors: {}, env: out.env });
    });
    // ---------- create ----------
    router.post(base, async (req, res) => {
        const body = req.body;
        const out = await store.run(ctxOf(req), async (repo) => {
            const lk = await repo.lookups();
            const env = await formEnv(repo, req, lk, null);
            const { values, errors } = await prepare(repo, req, lk, env, body, null);
            if (Object.keys(errors).length > 0)
                return { kind: 'invalid', env, errors };
            try {
                const id = await repo.create(key, values, metaOf(req));
                const visible = await repo.get(key, id);
                const owner = await repo.ownerById(Number(values.OwnerId));
                return { kind: 'saved', id, visible: visible !== null, ownerName: owner?.name ?? '' };
            }
            catch (err) {
                if (err instanceof errors_1.ForbiddenError)
                    return { kind: 'invalid', env, errors: { OwnerId: WRITE_BLOCKED } };
                throw err;
            }
        });
        if (out.kind === 'invalid')
            return renderForm(res, req, { title: `New ${def.singular}`, action: base, cancel: base, values: rawValues(body), errors: out.errors, env: out.env });
        afterSave(req, res, out);
    });
    const afterSave = (req, res, out) => {
        if (out.visible) {
            flash(req, `${cap(def.singular)} saved.`);
            return res.redirect(303, `${base}/${out.id}`);
        }
        flash(req, `Saved and assigned to ${out.ownerName}.`);
        res.redirect(303, base);
    };
    /** Validates input, owner choice and referenced records (all visible under RLS) inside the same transaction. */
    async function prepare(repo, req, lk, env, body, current) {
        const { values, errors } = (0, validators_1.validateEntity)(key, body, lk, current);
        const ownerId = Number(str(body.OwnerId));
        if (!errors.OwnerId) {
            const inList = env.owners.some((o) => o.id === ownerId);
            const keepsCurrent = current !== null && Number(current.OwnerId) === ownerId;
            const admin = ctxOf(req).role === 'Admin';
            if (!inList && !(keepsCurrent && admin)) {
                // A non-Admin cannot save a record that stays with an inactive (or out-of-team) owner: fn_RowWriteTarget rejects it.
                errors.OwnerId = admin ? 'Choose an owner.' : keepsCurrent && env.currentOwner && !env.currentOwner.active ? 'Current owner is inactive: choose a new owner.' : 'Choose an owner from your team.';
            }
        }
        {
            for (const [field, entity, message] of REF_FIELDS) {
                const id = values[field];
                if (typeof id === 'number' && !errors[field] && !(await repo.refExists(entity, id)))
                    errors[field] = message;
            }
        }
        return { values, errors };
    }
    const rawValues = (body) => Object.fromEntries(Object.entries(body).map(([k, v]) => [k, str(v)]));
    // ---------- detail ----------
    async function showDetail(req, res, id, extra = {}) {
        const tabParam = str(req.query.tab);
        const out = await store.run(ctxOf(req), async (repo) => {
            const row = await repo.get(key, id);
            if (!row)
                return null;
            const lk = await repo.lookups();
            const today = (0, format_1.todayIn)(lk.settings.timezone);
            const rel = async (entity, filter) => (await repo.list(entity, { q: '', page: 1, pageSize: 100, sort: entities_1.ENTITIES[entity].defaultSort, dir: 'asc', today, filters: { [filter]: String(id), ...(entity === 'opportunities' ? { status: 'all' } : {}), ...(entity === 'activities' ? { status: 'all' } : {}) } })).rows;
            const related = {};
            if (key === 'accounts') {
                related.contacts = await rel('contacts', 'account');
                related.opportunities = await rel('opportunities', 'account');
                related.activities = await rel('activities', 'account');
            }
            if (key === 'contacts') {
                related.opportunities = await rel('opportunities', 'contact');
                related.activities = await rel('activities', 'contact');
            }
            if (key === 'opportunities')
                related.activities = await rel('activities', 'opportunity');
            const history = await repo.history(key, id);
            return { row, lk, today, related, history };
        });
        if (!out)
            throw new errors_1.NotFoundError();
        const f = (0, format_1.makeFormatter)(out.lk.settings);
        const user = ctxOf(req);
        const tabs = Object.keys(out.related).map((e) => ({ key: e, label: entities_1.ENTITIES[e].plural, count: out.related[e].length }));
        const active = tabs.find((t) => t.key === tabParam) ?? tabs[0];
        const detailPath = `${base}/${id}`;
        const tabTable = active ? {
            entity: active.key, title: active.label,
            columns: (0, crmViews_1.columnsFor)(active.key).map((c) => c.label),
            rows: out.related[active.key].map((r) => ({ cells: (0, crmViews_1.cellsFor)(active.key, r, f, out.today, detailPath) })),
            newHref: `${entities_1.ENTITIES[active.key].path}/new?${key === 'accounts' ? 'accountId' : key === 'contacts' ? 'contactId' : 'opportunityId'}=${id}`,
            newLabel: active.key === 'activities' ? 'Log activity' : `New ${entities_1.ENTITIES[active.key].singular}`,
        } : null;
        const row = out.row;
        res.status(extra.lostError ? 422 : 200).render('pages/entity-detail', {
            title: `${cap(def.singular)} · ${(0, crmViews_1.recordName)(key, row)}`, activeNav: key, def, row, name: (0, crmViews_1.recordName)(key, row), details: (0, crmViews_1.detailRows)(key, row, f), canDelete: (0, crmViews_1.canDelete)(user, row),
            tabs: tabs.map((t) => ({ ...t, href: `${detailPath}?tab=${t.key}`, active: t.key === active?.key })), tabTable,
            modified: { by: String(row.ModifiedByName ?? ''), at: f.dateTime(String(row.ModifiedAt)), createdBy: String(row.CreatedByName ?? ''), createdAt: f.dateTime(String(row.CreatedAt)), edited: row.ModifiedAt !== row.CreatedAt || row.ModifiedBy !== row.CreatedBy },
            stageActions: key === 'opportunities' && row.StageType === 'open' ? { rowVer: String(row.RowVer), lostReasons: (out.lk.picklists.lost_reason ?? []).filter((p) => p.active), lostError: extra.lostError ?? '' } : null,
            doneAction: key === 'activities' ? { action: `${detailPath}/${row.IsDone ? 'undone' : 'done'}`, label: row.IsDone ? 'Mark as not done' : 'Mark as done', returnTo: detailPath } : null,
            history: out.history.map((h) => ({ at: f.dateTime(h.at), by: h.by, text: (0, crmViews_1.describeHistory)(h, f), owner: h.field === 'OwnerId' })),
            related: key === 'activities' ? (0, crmViews_1.relatedTo)(row) : null, typeLabel: crmViews_1.TYPE_LABEL[String(row.ActivityType)] ?? '',
        });
    }
    router.get(`${base}/:id`, (req, res) => showDetail(req, res, parseId(req.params.id)));
    // ---------- edit ----------
    router.get(`${base}/:id/edit`, async (req, res) => {
        const id = parseId(req.params.id);
        const out = await store.run(ctxOf(req), async (repo) => {
            const row = await repo.get(key, id);
            if (!row)
                return null;
            const lk = await repo.lookups();
            return { row, lk, env: await formEnv(repo, req, lk, row) };
        });
        if (!out)
            throw new errors_1.NotFoundError();
        const f = (0, format_1.makeFormatter)(out.lk.settings);
        renderForm(res, req, { title: `Edit ${(0, crmViews_1.recordName)(key, out.row)}`, action: `${base}/${id}`, cancel: `${base}/${id}`, values: (0, crmViews_1.formValuesFromRow)(key, out.row, f), errors: {}, env: out.env, rowVer: String(out.row.RowVer) });
    });
    // ---------- update ----------
    router.post(`${base}/:id`, async (req, res) => {
        const id = parseId(req.params.id);
        const body = req.body;
        const rowVer = str(body.rowVer);
        const out = await store.run(ctxOf(req), async (repo) => {
            const current = await repo.get(key, id);
            if (!current)
                return { kind: 'notfound' };
            const lk = await repo.lookups();
            const env = await formEnv(repo, req, lk, current);
            const { values, errors } = await prepare(repo, req, lk, env, body, current);
            if (Object.keys(errors).length > 0)
                return { kind: 'invalid', env, errors, name: (0, crmViews_1.recordName)(key, current) };
            let result;
            try {
                result = await repo.update(key, id, values, rowVer, metaOf(req));
            }
            catch (err) {
                // The database refused the resulting row (33504): keep the user's input and say why.
                if (err instanceof errors_1.ForbiddenError)
                    return { kind: 'invalid', env, errors: { OwnerId: WRITE_BLOCKED }, name: (0, crmViews_1.recordName)(key, current) };
                throw err;
            }
            if (result !== 'ok')
                return { kind: result };
            const visible = await repo.get(key, id);
            const owner = await repo.ownerById(Number(values.OwnerId));
            return { kind: 'saved', visible: visible !== null, ownerName: owner?.name ?? '' };
        });
        if (out.kind === 'notfound')
            throw new errors_1.NotFoundError();
        if (out.kind === 'conflict')
            throw new errors_1.EditConflictError(`${base}/${id}`);
        if (out.kind === 'invalid')
            return renderForm(res, req, { title: `Edit ${out.name}`, action: `${base}/${id}`, cancel: `${base}/${id}`, values: rawValues(body), errors: out.errors, env: out.env, rowVer });
        if (out.kind !== 'saved')
            throw new errors_1.NotFoundError();
        afterSave(req, res, { id, visible: out.visible, ownerName: out.ownerName });
    });
    // ---------- delete ----------
    router.get(`${base}/:id/delete`, async (req, res) => {
        const id = parseId(req.params.id);
        const row = await store.run(ctxOf(req), (repo) => repo.get(key, id));
        if (!row)
            throw new errors_1.NotFoundError();
        if (!(0, crmViews_1.canDelete)(ctxOf(req), row))
            throw new errors_1.ForbiddenError();
        res.render('pages/entity-delete', { title: `Delete ${(0, crmViews_1.recordName)(key, row)}`, activeNav: key, def, name: (0, crmViews_1.recordName)(key, row), id, notice: deleteNotice(key) });
    });
    router.post(`${base}/:id/delete`, async (req, res) => {
        const id = parseId(req.params.id);
        try {
            const done = await store.run(ctxOf(req), async (repo) => {
                const row = await repo.get(key, id);
                if (!row)
                    return false;
                return repo.remove(key, id, metaOf(req));
            });
            if (!done)
                throw new errors_1.NotFoundError();
        }
        catch (err) {
            if (err instanceof errors_1.ConflictError && !(err instanceof errors_1.EditConflictError))
                throw new errors_1.ConflictError(FK_MESSAGE[key], { cause: err });
            throw err;
        }
        flash(req, `${cap(def.singular)} deleted.`);
        res.redirect(303, base);
    });
    // ---------- opportunity: won / lost (with a confirmation step) ----------
    if (key === 'opportunities') {
        for (const outcome of ['won', 'lost']) {
            router.post(`${base}/:id/${outcome}`, async (req, res) => {
                const id = parseId(req.params.id);
                const body = req.body;
                const confirmed = str(body.confirm) === '1';
                const rowVer = str(body.rowVer);
                const out = await store.run(ctxOf(req), async (repo) => {
                    const row = await repo.get(key, id);
                    if (!row)
                        return { kind: 'notfound' };
                    if (!/^0x[0-9A-Fa-f]{16}$/.test(rowVer))
                        return { kind: 'conflict' }; // the action must carry the RowVer of the page it was clicked on
                    const lk = await repo.lookups();
                    const stage = lk.stages.find((s) => s.type === outcome);
                    if (!stage)
                        return { kind: 'error', message: `No ${outcome} stage is configured.` };
                    const lostReason = Number(str(body.lostReason));
                    const reason = (lk.picklists.lost_reason ?? []).find((p) => p.active && p.id === lostReason);
                    if (outcome === 'lost' && !reason)
                        return { kind: 'lostError' };
                    const today = (0, format_1.todayIn)(lk.settings.timezone);
                    const moved = String(row.CloseDate) > today;
                    if (!confirmed)
                        return { kind: 'confirm', name: (0, crmViews_1.recordName)(key, row), stage: stage.name, moved, reason: reason?.label ?? '' };
                    const values = Object.fromEntries(def.columns.map((c) => [c.name, row[c.name] ?? null]));
                    Object.assign(values, { StageId: stage.id, LostReasonValueId: outcome === 'lost' ? lostReason : null, OwnerId: row.OwnerId });
                    if (moved)
                        values.CloseDate = today;
                    const result = await repo.update(key, id, values, rowVer, metaOf(req));
                    return result === 'ok' ? { kind: 'ok', moved } : { kind: result };
                });
                if (out.kind === 'notfound')
                    throw new errors_1.NotFoundError();
                if (out.kind === 'conflict')
                    throw new errors_1.EditConflictError(`${base}/${id}`);
                if (out.kind === 'lostError')
                    return showDetail(req, res, id, { lostError: 'Choose why this was lost.' });
                if (out.kind === 'confirm') {
                    const msg = `Mark ${out.name} as ${outcome}? The stage changes to ${out.stage}${outcome === 'lost' ? ` (reason: ${out.reason})` : ''}.${out.moved ? ' The close date will be set to today.' : ''}`;
                    return res.render('pages/confirm', { title: `Mark as ${outcome}`, activeNav: key, message: msg, action: `${base}/${id}/${outcome}`, fields: [{ name: 'rowVer', value: rowVer }, { name: 'lostReason', value: str(body.lostReason) }, { name: 'confirm', value: '1' }], label: `Mark as ${outcome}`, danger: false, cancel: `${base}/${id}` });
                }
                if (out.kind === 'error')
                    flash(req, out.message, 'error');
                else
                    flash(req, `Opportunity marked as ${outcome}.${out.moved ? ' Close date set to today.' : ''}`);
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
                if (!ok)
                    throw new errors_1.NotFoundError();
                flash(req, done ? 'Marked as done.' : 'Marked as not done.');
                res.redirect(303, (0, returnTo_1.safeReturnTo)(req.body.returnTo, base));
            });
        }
    }
}
function deleteNotice(key) {
    switch (key) {
        case 'accounts': return "This also removes the link between this account and its contacts and activities. The records themselves are kept.";
        case 'contacts': return 'This also removes the link between this contact and its opportunities and activities.';
        case 'opportunities': return 'This also removes the link between this opportunity and its activities.';
        case 'activities': return '';
    }
}
//# sourceMappingURL=crm.js.map