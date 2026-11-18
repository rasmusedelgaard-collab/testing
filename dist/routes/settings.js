"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.createSettingsRouter = createSettingsRouter;
const multer_1 = __importDefault(require("multer"));
const express_1 = require("express");
const settingsValidators_1 = require("../domain/settingsValidators");
const format_1 = require("../services/format");
const errors_1 = require("../errors");
const csrf_1 = require("../middleware/csrf");
const logo_1 = require("../services/logo");
const esc = (t) => t.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const str = (v) => (typeof v === 'string' ? v : '');
const parseId = (raw) => {
    if (typeof raw !== 'string' || !/^[1-9]\d{0,9}$/.test(raw) || Number(raw) > 2_147_483_647)
        throw new errors_1.NotFoundError();
    return Number(raw);
};
const PICKLIST_CODES = ['industry', 'activity_type', 'lead_source', 'lost_reason'];
const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
/** Admin-only settings pages. The router is mounted behind requireAuth + requireRole('Admin'); fn_AdminWrite is the DB backstop. */
function createSettingsRouter(store, branding) {
    const router = (0, express_1.Router)();
    const ctxOf = (req) => {
        if (!req.rls)
            throw new errors_1.ForbiddenError();
        return req.rls;
    };
    const meta = (req) => ({ userId: ctxOf(req).userId, correlationId: req.id });
    const flash = (req, message, kind = 'success') => { req.session.flash = { kind, message }; };
    const summaryOf = (errors) => Object.entries(errors).map(([field, message]) => ({ field, message }));
    const render = (res, view, nav, data) => {
        const errors = (data.errors ?? {});
        const status = Object.keys(errors).length > 0 || (Array.isArray(data.summary) && data.summary.length > 0) ? 422 : 200;
        // Shared field-error pattern: inline message with icon and id, plus aria-invalid/aria-describedby on the input.
        const errHtml = (name) => (errors[name] ? `<span class="field-error" id="e-${esc(name)}"><span aria-hidden="true">⚠ </span>${esc(errors[name])}</span>` : '');
        const invalidAttrs = (name) => (errors[name] ? ` aria-invalid="true" aria-describedby="e-${esc(name)}"` : '');
        res.status(status).render(`pages/${view}`, { activeNav: 'settings', settingsNav: nav, errors, summary: summaryOf(errors), errHtml, invalidAttrs, ...data });
    };
    router.get('/', (_req, res) => res.redirect(302, '/settings/general'));
    // ---------------- General / branding ----------------
    router.get('/general', async (req, res) => {
        const out = await store.runAdmin(ctxOf(req), async (a) => ({ s: await a.settingsMap(), logo: await a.logoInfo() }));
        render(res, 'settings-general', 'general', { title: 'General settings', values: { appName: out.s['app.name'] ?? 'CRM', accentColor: out.s['branding.accentColor'] ?? '#1D4ED8' }, logo: out.logo });
    });
    const logoError = async (req, res, message) => {
        const out = await store.runAdmin(ctxOf(req), async (a) => ({ s: await a.settingsMap(), logo: await a.logoInfo() }));
        render(res, 'settings-general', 'general', { title: 'General settings', values: { appName: out.s['app.name'] ?? 'CRM', accentColor: out.s['branding.accentColor'] ?? '#1D4ED8' }, logo: out.logo, errors: { logo: message } });
    };
    router.post('/general', async (req, res) => {
        const { values, errors } = (0, settingsValidators_1.validateGeneral)(req.body);
        if (Object.keys(errors).length > 0) {
            const logo = await store.runAdmin(ctxOf(req), (a) => a.logoInfo());
            return render(res, 'settings-general', 'general', { title: 'General settings', values: { appName: str(req.body.appName), accentColor: str(req.body.accentColor) }, errors, logo });
        }
        await store.runAdmin(ctxOf(req), (a) => a.saveSettings({ 'app.name': values.appName, 'branding.accentColor': values.accentColor }, meta(req)));
        branding.invalidate();
        flash(req, 'Settings saved.');
        res.redirect(303, '/settings/general');
    });
    // Logo upload: memory storage only, strict limits, content sniffed (never the filename or declared type).
    const upload = (0, multer_1.default)({ storage: multer_1.default.memoryStorage(), limits: { fileSize: logo_1.MAX_LOGO_BYTES, files: 1, fields: 4, parts: 6, fieldSize: 512 } });
    router.post('/general/logo', (req, res, next) => {
        upload.single('logo')(req, res, async (err) => {
            try {
                // csrfProtection skipped this multipart route, so the token is verified here, before anything is stored.
                if (!(0, csrf_1.csrfValid)(req, req.body?._csrf))
                    return next(new errors_1.ForbiddenError('Invalid or missing CSRF token'));
                if (err) {
                    return await logoError(req, res, 'Logo must be a PNG, 500 KB or smaller.');
                }
                const file = req.file;
                if (!file)
                    return await logoError(req, res, 'Choose a logo file first.');
                const check = (0, logo_1.checkLogo)(file.buffer);
                if (!check.ok)
                    return await logoError(req, res, check.message);
                await store.runAdmin(ctxOf(req), (a) => a.saveLogo(check.contentType, file.buffer, check.sha256, meta(req)));
                branding.invalidate();
                flash(req, 'Logo saved.');
                res.redirect(303, '/settings/general');
            }
            catch (e) {
                next(e);
            }
        });
    });
    // ---------------- Currency & locale ----------------
    const warningsFor = (enabled, usage) => Object.entries(usage).filter(([c, n]) => n > 0 && !enabled.includes(c)).map(([c, n]) => `${c} is disabled but ${n} ${n === 1 ? 'opportunity still uses' : 'opportunities still use'} it. They keep their currency; new records can't use it.`);
    const localePage = (res, d) => {
        const base = String(d.values.base ?? 'DKK');
        const locale = String(d.values.locale ?? 'en-GB');
        let preview = '';
        try {
            const f = (0, format_1.makeFormatter)({ localeCode: locale, timezone: String(d.values.timezone ?? 'UTC') });
            preview = `${new Intl.NumberFormat(locale, { style: 'currency', currency: settingsValidators_1.CURRENCIES.includes(base) ? base : 'DKK' }).format(1234567.89)} · ${f.date((0, format_1.todayIn)(String(d.values.timezone ?? 'UTC')))}`;
        }
        catch { /* preview is cosmetic */ }
        render(res, 'settings-locale', 'locale', { title: 'Currency & locale', currencies: settingsValidators_1.CURRENCIES, locales: format_1.ALLOWED_LOCALES, zones: (0, settingsValidators_1.timezones)(), days: DAYS, months: MONTHS, preview, ...d });
    };
    router.get('/locale', async (req, res) => {
        const { s, usage } = await store.runAdmin(ctxOf(req), async (a) => ({ s: await a.settingsMap(), usage: await a.currencyUsage() }));
        localePage(res, { s, warnings: warningsFor((s['currency.enabled'] ?? '').split(','), usage), values: { base: s['currency.base'], enabled: (s['currency.enabled'] ?? '').split(','), locale: s['locale.code'], timezone: s['locale.timezone'], firstDay: s['locale.firstDayOfWeek'], fiscalStart: s['fiscal.startMonth'] } });
    });
    router.post('/locale', async (req, res) => {
        const { values, errors } = (0, settingsValidators_1.validateLocale)(req.body);
        if (Object.keys(errors).length > 0)
            return localePage(res, { s: {}, values: { ...values, enabled: values.enabled, firstDay: str(req.body.firstDay), fiscalStart: str(req.body.fiscalStart) }, errors });
        const usage = await store.runAdmin(ctxOf(req), async (a) => {
            await a.saveSettings({
                'currency.base': values.base, 'currency.enabled': values.enabled.join(','), 'locale.code': values.locale, 'locale.timezone': values.timezone,
                'locale.firstDayOfWeek': String(values.firstDay), 'fiscal.startMonth': String(values.fiscalStart),
            }, meta(req));
            return a.currencyUsage();
        });
        const warn = warningsFor(values.enabled, usage);
        flash(req, warn.length ? `Settings saved. ${warn.join(' ')}` : 'Settings saved.');
        res.redirect(303, '/settings/locale');
    });
    // ---------------- FX rates ----------------
    const monthLabel = (m) => `${m.slice(5, 7)}.${m.slice(0, 4)}`;
    async function fxPage(req, res, over = {}) {
        const out = await store.runAdmin(ctxOf(req), async (a) => ({ lk: await a.lookups(), rates: await a.fxRates() }));
        const st = out.lk.settings;
        const f = (0, format_1.makeFormatter)(st);
        const curMonth = (0, format_1.todayIn)(st.timezone).slice(0, 7);
        const wantCur = str(req.query.currency).toUpperCase();
        const wantYear = /^\d{4}$/.test(str(req.query.year)) ? str(req.query.year) : '';
        const rates = out.rates.filter((r) => (!wantCur || r.currency === wantCur) && (!wantYear || r.month.startsWith(wantYear)));
        const others = st.enabledCurrencies.filter((c) => c !== st.baseCurrency);
        const missing = others.filter((c) => !out.rates.some((r) => r.currency === c && r.month === curMonth));
        const nf = new Intl.NumberFormat(st.localeCode, { minimumFractionDigits: 4, maximumFractionDigits: 6 });
        render(res, 'settings-fx', 'fx', {
            title: 'FX rates', base: st.baseCurrency, currencies: others, curMonth, curLabel: monthLabel(curMonth), years: [...new Set([...out.rates.map((r) => r.month.slice(0, 4)), curMonth.slice(0, 4)])].sort().reverse(), wantCur, wantYear,
            rows: rates.map((r) => ({ ...r, monthLabel: monthLabel(r.month), rateText: nf.format(r.rate), at: f.dateTime(r.modifiedAt) })),
            missing, editId: over.editId ?? null, dupId: over.dupId ?? null,
            values: over.values ?? { currency: '', month: curMonth, rate: '' }, errors: over.errors ?? {},
        });
    }
    router.get('/fx', (req, res) => fxPage(req, res));
    router.get('/fx/:id/edit', async (req, res) => {
        const id = parseId(req.params.id);
        const out = await store.runAdmin(ctxOf(req), async (a) => ({ r: await a.fxRate(id), lk: await a.lookups() }));
        if (!out.r)
            throw new errors_1.NotFoundError();
        await fxPage(req, res, { editId: id, values: { currency: out.r.currency, month: out.r.month, rate: (0, format_1.rateInput)(out.r.rate, out.lk.settings.localeCode) } });
    });
    const saveFx = async (req, res, id) => {
        const body = req.body;
        const out = await store.runAdmin(ctxOf(req), async (a) => {
            const lk = await a.lookups();
            const v = (0, settingsValidators_1.validateFx)(body, { base: lk.settings.baseCurrency, enabled: lk.settings.enabledCurrencies, locale: lk.settings.localeCode });
            if (Object.keys(v.errors).length > 0)
                return { kind: 'invalid', errors: v.errors };
            if (id !== null && str(body.confirm) !== '1') {
                // Changing an existing rate restates totals: ask first, unless nothing changes.
                const old = await a.fxRate(id);
                const clash = (await a.fxRates()).some((x) => x.id !== id && x.currency === v.values.currency && x.month === v.values.month);
                if (!clash && old && (old.currency !== v.values.currency || old.month !== v.values.month || old.rate !== v.values.rate))
                    return { kind: 'confirm', month: old.month };
            }
            const r = await a.saveFxRate({ id, base: lk.settings.baseCurrency, currency: v.values.currency, month: v.values.month, rate: v.values.rate }, meta(req));
            if (!r.ok)
                return { kind: 'dup', dupId: r.duplicateOf, values: v.values, name: `${v.values.currency} in ${monthLabel(v.values.month)}` };
            return { kind: 'ok' };
        });
        const raw = { currency: str(body.currency), month: str(body.month), rate: str(body.rate) };
        if (out.kind === 'confirm') {
            return render(res, 'settings-confirm', 'fx', { title: 'Change rate', message: `This changes totals on the dashboard and in analytics for ${monthLabel(out.month)}. Continue?`, action: `/settings/fx/${id}`, fields: [{ name: 'currency', value: raw.currency }, { name: 'month', value: raw.month }, { name: 'rate', value: raw.rate }, { name: 'confirm', value: '1' }], label: 'Save rate', danger: false, cancel: '/settings/fx' });
        }
        if (out.kind === 'invalid')
            return fxPage(req, res, { values: raw, errors: out.errors, editId: id });
        if (out.kind === 'dup')
            return fxPage(req, res, { values: raw, editId: id, dupId: out.dupId, errors: { currency: `A rate for ${out.name} already exists.` } });
        flash(req, 'Rate saved.');
        res.redirect(303, '/settings/fx');
    };
    router.post('/fx', (req, res) => saveFx(req, res, null));
    router.post('/fx/:id', (req, res) => saveFx(req, res, parseId(req.params.id)));
    router.get('/fx/:id/delete', async (req, res) => {
        const id = parseId(req.params.id);
        const r = await store.runAdmin(ctxOf(req), (a) => a.fxRate(id));
        if (!r)
            throw new errors_1.NotFoundError();
        render(res, 'settings-confirm', 'fx', { title: 'Delete rate', message: `Delete the ${r.currency} rate for ${monthLabel(r.month)}? This changes totals on the dashboard and in analytics for ${monthLabel(r.month)}. Continue?`, action: `/settings/fx/${id}/delete`, fields: [], label: 'Delete rate', danger: true, cancel: '/settings/fx' });
    });
    router.post('/fx/:id/delete', async (req, res) => {
        const id = parseId(req.params.id);
        if (!(await store.runAdmin(ctxOf(req), (a) => a.deleteFxRate(id, meta(req)))))
            throw new errors_1.NotFoundError();
        flash(req, 'Rate deleted.');
        res.redirect(303, '/settings/fx');
    });
    // ---------------- Pipeline stages ----------------
    const stageRowsFromBody = (body) => {
        const n = Math.min(Math.max(Number(str(body.n)) || 0, 0), 50);
        return Array.from({ length: n }, (_, i) => ({ id: str(body[`id_${i}`]), name: str(body[`name_${i}`]), prob: str(body[`prob_${i}`]), type: str(body[`type_${i}`]) }));
    };
    async function stagesPage(req, res, rows, usage, extra = {}) {
        const used = new Map(usage.map((u) => [String(u.id), u.used]));
        const errors = extra.errors ?? {};
        // One summary for everything: row problems first (each links to its field, prefixed with the row number), then page-level ones.
        const rowItems = Object.entries(errors).map(([field, message]) => ({ field, message: `Row ${Number(field.replace(/\D/g, '')) + 1}: ${message}` }));
        const summary = [...rowItems, ...(extra.summary ?? []).map((message) => ({ field: '', message }))];
        render(res, 'settings-stages', 'stages', { title: 'Pipeline stages', rows: rows.map((r) => ({ ...r, used: used.get(r.id) ?? 0 })), errors, summary, note: extra.note ?? '', focus: extra.focus ?? '', links: extra.links ?? {} });
    }
    router.get('/stages', async (req, res) => {
        const usage = await store.runAdmin(ctxOf(req), (a) => a.stageUsage());
        await stagesPage(req, res, usage.map((u) => ({ id: String(u.id), name: u.name, prob: String(u.probability), type: u.type })), usage);
    });
    router.post('/stages', async (req, res) => {
        const body = req.body;
        const action = str(body.action);
        let rows = stageRowsFromBody(body);
        const m = /^(up|down|remove):(\d{1,2})$/.exec(action);
        if (action === 'add' || m) {
            const usage = await store.runAdmin(ctxOf(req), (a) => a.stageUsage());
            let focus = '';
            if (action === 'add') {
                rows.push({ id: '', name: '', prob: '10', type: 'open' });
                focus = `f-name${rows.length - 1}`;
            }
            else if (m) {
                const i = Number(m[2]);
                const row = rows[i];
                if (m[1] === 'remove' && row) {
                    // Refuse right here (the row stays) instead of failing at Save and losing the row and other unsaved edits.
                    const inUse = usage.find((u) => String(u.id) === row.id)?.used ?? 0;
                    const sameType = rows.filter((r) => r.type === row.type).length;
                    if (inUse > 0) {
                        const name = row.name || 'This stage';
                        return stagesPage(req, res, rows, usage, { errors: { [`name${i}`]: `Can't remove "${name}": ${inUse} ${inUse === 1 ? 'opportunity uses' : 'opportunities use'} it. Move ${inUse === 1 ? 'it' : 'them'} to another stage first.` }, links: { [`name${i}`]: `/opportunities?stage=${row.id}&status=all` }, focus: `f-name${i}` });
                    }
                    if ((row.type === 'won' || row.type === 'lost') && sameType <= 1) {
                        return stagesPage(req, res, rows, usage, { errors: { [`name${i}`]: `Can't remove the only ${row.type === 'won' ? 'Won' : 'Lost'} stage. Make another stage ${row.type === 'won' ? 'Won' : 'Lost'} first.` }, focus: `f-name${i}` });
                    }
                    rows = rows.filter((_, k) => k !== i);
                    focus = `f-name${Math.min(i, rows.length - 1)}`;
                }
                else if (m[1] !== 'remove' && row) {
                    const j = m[1] === 'up' ? i - 1 : i + 1;
                    if (rows[j]) {
                        [rows[i], rows[j]] = [rows[j], rows[i]];
                        // Keep the focus on the same button of the moved row, or the opposite one when it reached an end.
                        const atEnd = m[1] === 'up' ? j === 0 : j === rows.length - 1;
                        focus = `${atEnd ? (m[1] === 'up' ? 'down' : 'up') : m[1]}-${j}`;
                    }
                }
            }
            return stagesPage(req, res, rows, usage, { note: 'Changes are not saved yet. Choose Save stages to keep them.', focus });
        }
        const v = (0, settingsValidators_1.validateStages)(rows);
        const result = await store.runAdmin(ctxOf(req), async (a) => {
            const usage = await a.stageUsage();
            if (Object.keys(v.errors).length > 0 || v.summary.length > 0)
                return { kind: 'invalid', usage };
            const r = await a.saveStages(v.rows, meta(req));
            return r.ok ? { kind: 'ok', usage } : { kind: 'fail', usage, error: r.error };
        });
        if (result.kind === 'invalid')
            return stagesPage(req, res, rows, result.usage, { errors: v.errors, summary: v.summary });
        if (result.kind === 'fail')
            return stagesPage(req, res, rows, result.usage, { summary: [result.error] });
        flash(req, 'Settings saved.');
        res.redirect(303, '/settings/stages');
    });
    // ---------------- Picklists ----------------
    router.get('/picklists', (_req, res) => res.redirect(302, '/settings/picklists/industry'));
    async function picklistPage(req, res, code, errors = {}, values = {}) {
        if (!PICKLIST_CODES.includes(code))
            throw new errors_1.NotFoundError();
        const list = await store.runAdmin(ctxOf(req), (a) => a.picklist(code));
        if (!list)
            throw new errors_1.NotFoundError();
        const fm = /^([1-9]\d{0,9})-(up|down)$/.exec(str(req.query.focus));
        // After a move keep the focus on the same button of the moved value (or the other one when it hit an end).
        let focus = '';
        if (fm) {
            const i = list.values.findIndex((v) => String(v.id) === fm[1]);
            const atEnd = fm[2] === 'up' ? i === 0 : i === list.values.length - 1;
            focus = i < 0 ? '' : `${atEnd ? (fm[2] === 'up' ? 'down' : 'up') : fm[2]}-${fm[1]}`;
        }
        render(res, 'settings-picklists', 'picklists', { title: `Picklist: ${list.name}`, code, list, codes: PICKLIST_CODES, canAdd: code !== 'activity_type', errors, values, focus });
    }
    router.get('/picklists/:code', (req, res) => picklistPage(req, res, str(req.params.code)));
    router.post('/picklists/:code', async (req, res) => {
        const code = str(req.params.code);
        if (!PICKLIST_CODES.includes(code))
            throw new errors_1.NotFoundError();
        const body = req.body;
        const action = str(body.action);
        const id = action === 'add' ? 0 : parseId(body.valueId);
        const label = str(body.label).trim();
        const res2 = await store.runAdmin(ctxOf(req), async (a) => {
            if (action === 'add' || action === 'rename') {
                const bad = (0, settingsValidators_1.validateLabel)(label);
                if (bad)
                    return { ok: false, error: bad, field: action === 'add' ? 'label' : `label${id}` };
                return action === 'add' ? a.addPicklistValue(code, label, meta(req)) : a.renamePicklistValue(code, id, label, meta(req));
            }
            if (action === 'deactivate' || action === 'activate')
                return a.setPicklistActive(code, id, action === 'activate', meta(req));
            if (action === 'up' || action === 'down')
                return a.movePicklistValue(code, id, action, meta(req));
            return { ok: false, error: 'Unknown action.' };
        });
        if (!res2.ok)
            return picklistPage(req, res, code, { [res2.field ?? 'label']: res2.error }, { label });
        flash(req, 'Settings saved.');
        res.redirect(303, `/settings/picklists/${code}${action === 'up' || action === 'down' ? `?focus=${id}-${action}` : ''}`);
    });
    // ---------------- Visibility ----------------
    router.get('/visibility', async (req, res) => {
        const lk = await store.runAdmin(ctxOf(req), (a) => a.lookups());
        render(res, 'settings-visibility', 'visibility', { title: 'Visibility', mode: lk.settings.visibilityMode });
    });
    router.post('/visibility', async (req, res) => {
        const mode = str(req.body.mode);
        if (mode !== 'private' && mode !== 'team') {
            const lk = await store.runAdmin(ctxOf(req), (a) => a.lookups());
            return render(res, 'settings-visibility', 'visibility', { title: 'Visibility', mode: lk.settings.visibilityMode, errors: { mode: 'Choose who can see records.' } });
        }
        const current = (await store.runAdmin(ctxOf(req), (a) => a.lookups())).settings.visibilityMode;
        if (mode !== current && str(req.body.confirm) !== '1') {
            return render(res, 'settings-confirm', 'visibility', { title: 'Change visibility', message: 'This changes what everyone can see immediately. Continue?', action: '/settings/visibility', fields: [{ name: 'mode', value: mode }, { name: 'confirm', value: '1' }], label: 'Change visibility', danger: false, cancel: '/settings/visibility' });
        }
        await store.runAdmin(ctxOf(req), (a) => a.saveSettings({ 'visibility.mode': mode }, meta(req)));
        flash(req, 'Settings saved.');
        res.redirect(303, '/settings/visibility');
    });
    // ---------------- Users & teams ----------------
    async function usersPage(req, res, over = {}) {
        const out = await store.runAdmin(ctxOf(req), async (a) => ({ users: await a.users(), teams: await a.teams(), lk: await a.lookups() }));
        const f = (0, format_1.makeFormatter)(out.lk.settings);
        const role = ['Admin', 'Manager', 'User'].includes(str(req.query.role)) ? str(req.query.role) : '';
        const team = str(req.query.team);
        const q = str(req.query.q).trim().toLowerCase().slice(0, 100);
        const all = out.users;
        const users = all.filter((u) => (!role || u.role === role) && (!team || (team === 'none' ? u.teamId === null : String(u.teamId) === team)) && (!q || u.name.toLowerCase().includes(q) || (u.email ?? '').toLowerCase().includes(q)));
        const badge = (u) => u.teamStatus === 'multiple' ? 'In several teams — check Entra groups' : u.teamStatus === 'overage' ? 'Too many groups — team not applied' : u.teamStatus === 'none' || u.teamId === null ? 'No team — sees own records only' : '';
        render(res, 'settings-users', 'users', {
            title: 'Users & teams', teams: out.teams, filters: { role, team, q }, connection: { ip: req.ip ?? 'unknown', hops: String(req.app.get('trust proxy')) },
            users: users.map((u) => ({ ...u, lastSignInText: u.lastSignIn ? f.dateTime(u.lastSignIn) : '', badge: badge(u), teamLabel: u.teamName ?? '' })),
            noTeamCount: all.filter((u) => u.active && badge(u) !== '').length, errors: over.errors ?? {}, values: over.values ?? {},
        });
    }
    router.get('/users', (req, res) => usersPage(req, res));
    router.post('/teams', async (req, res) => {
        const { values, errors } = (0, settingsValidators_1.validateTeam)(req.body);
        if (Object.keys(errors).length > 0)
            return usersPage(req, res, { errors, values: { name: str(req.body.name), groupId: str(req.body.groupId) } });
        const r = await store.runAdmin(ctxOf(req), (a) => a.saveTeam({ id: null, ...values }, meta(req)));
        if (!r.ok)
            return usersPage(req, res, { errors: { [r.field ?? 'name']: r.error }, values: { name: values.name, groupId: values.groupId } });
        flash(req, `Team ${values.name} added.`);
        res.redirect(303, '/settings/users');
    });
    router.get('/teams/:id/edit', async (req, res) => {
        const id = parseId(req.params.id);
        const t = await store.runAdmin(ctxOf(req), (a) => a.team(id));
        if (!t)
            throw new errors_1.NotFoundError();
        render(res, 'settings-team', 'users', { title: `Edit ${t.name}`, team: t, values: { name: t.name, groupId: t.groupId } });
    });
    router.post('/teams/:id', async (req, res) => {
        const id = parseId(req.params.id);
        const { values, errors } = (0, settingsValidators_1.validateTeam)(req.body);
        const t = await store.runAdmin(ctxOf(req), (a) => a.team(id));
        if (!t)
            throw new errors_1.NotFoundError();
        if (Object.keys(errors).length > 0)
            return render(res, 'settings-team', 'users', { title: `Edit ${t.name}`, team: t, values: { name: str(req.body.name), groupId: str(req.body.groupId) }, errors });
        if (values.groupId !== t.groupId && str(req.body.confirm) !== '1') {
            return render(res, 'settings-confirm', 'users', { title: 'Change group', message: `Changing the group changes who is in ${t.name}. People are moved at their next sign-in. Continue?`, action: `/settings/teams/${id}`, fields: [{ name: 'name', value: values.name }, { name: 'groupId', value: values.groupId }, { name: 'confirm', value: '1' }], label: 'Save team', danger: false, cancel: '/settings/users' });
        }
        const r = await store.runAdmin(ctxOf(req), (a) => a.saveTeam({ id, ...values }, meta(req)));
        if (!r.ok)
            return render(res, 'settings-team', 'users', { title: `Edit ${t.name}`, team: t, values, errors: { [r.field ?? 'name']: r.error } });
        flash(req, `Team ${values.name} saved.`);
        res.redirect(303, '/settings/users');
    });
    router.get('/teams/:id/deactivate', async (req, res) => {
        const id = parseId(req.params.id);
        const t = await store.runAdmin(ctxOf(req), (a) => a.team(id));
        if (!t)
            throw new errors_1.NotFoundError();
        render(res, 'settings-confirm', 'users', { title: 'Deactivate team', message: `Deactivate ${t.name}? Its ${t.members} members will have no team from their next sign-in and will see only their own records. Their records keep their owners.`, action: `/settings/teams/${id}/deactivate`, fields: [], label: 'Deactivate team', danger: true, cancel: '/settings/users' });
    });
    const setActive = (active) => async (req, res) => {
        const id = parseId(req.params.id);
        const t = await store.runAdmin(ctxOf(req), async (a) => { const x = await a.team(id); if (x)
            await a.setTeamActive(id, active, meta(req)); return x; });
        if (!t)
            throw new errors_1.NotFoundError();
        flash(req, `Team ${t.name} ${active ? 'reactivated' : 'deactivated'}.`);
        res.redirect(303, '/settings/users');
    };
    router.post('/teams/:id/deactivate', setActive(false));
    router.post('/teams/:id/reactivate', setActive(true));
    return router;
}
//# sourceMappingURL=settings.js.map