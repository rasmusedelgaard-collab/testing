"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.MemoryAdminRepo = void 0;
const PICKLISTS = { industry: 'Industry', lead_source: 'Lead source', lost_reason: 'Lost reason', activity_type: 'Activity type' };
const KEYS = [
    ['app.name', (s) => s.appName, (s, v) => { s.appName = v; }],
    ['branding.accentColor', (s) => s.accentColor, (s, v) => { s.accentColor = v; }],
    ['locale.code', (s) => s.localeCode, (s, v) => { s.localeCode = v; }],
    ['locale.timezone', (s) => s.timezone, (s, v) => { s.timezone = v; }],
    ['locale.firstDayOfWeek', (s) => String(s.firstDayOfWeek), (s, v) => { s.firstDayOfWeek = Number(v); }],
    ['fiscal.startMonth', (s) => String(s.fiscalStartMonth), (s, v) => { s.fiscalStartMonth = Number(v); }],
    ['currency.base', (s) => s.baseCurrency, (s, v) => { s.baseCurrency = v; }],
    ['currency.enabled', (s) => s.enabledCurrencies.join(','), (s, v) => { s.enabledCurrencies = v.split(','); }],
    ['visibility.mode', (s) => s.visibilityMode, (s, v) => { s.visibilityMode = v === 'team' ? 'team' : 'private'; }],
];
/** In-memory twin of SqlAdminRepo for demo mode and tests. Writes audit rows like the SQL version. */
class MemoryAdminRepo {
    s;
    ctx;
    nextFx = 1;
    constructor(s, ctx) {
        this.s = s;
        this.ctx = ctx;
    }
    log(type, id, action, field, o, n, meta) {
        const row = { entityType: type, entityId: id, action, fieldName: field, oldValue: o, newValue: n, ownerId: meta.userId, teamId: null };
        this.s.audit.push({ ...row, changedBy: meta.userId, correlationId: meta.correlationId, at: new Date().toISOString() });
    }
    async lookups() { return { settings: this.s.settings, stages: this.s.stages, picklists: this.s.picklists }; }
    async settingsMap() { return Object.fromEntries(KEYS.map(([k, get]) => [k, get(this.s.settings)])); }
    async saveSettings(updates, meta) {
        for (const [key, get, set] of KEYS) {
            if (!(key in updates))
                continue;
            const old = get(this.s.settings);
            const nv = updates[key];
            if (old === nv)
                continue;
            set(this.s.settings, nv);
            this.log('setting', KEYS.findIndex((k) => k[0] === key) + 1, 'U', key, old, nv, meta);
        }
    }
    async logoInfo() { return this.s.logo ? { contentType: this.s.logo.contentType, sha256: this.s.logo.sha256 } : null; }
    async saveLogo(contentType, content, sha256, meta) {
        const old = this.s.logo?.sha256 ?? null;
        this.s.logo = { contentType, content, sha256 };
        this.log('branding', 1, old ? 'U' : 'C', 'logo', old, sha256, meta);
    }
    toFx(f) { return { ...f }; }
    async fxRates() { return this.s.fx.map(this.toFx).sort((a, b) => b.month.localeCompare(a.month) || a.currency.localeCompare(b.currency)); }
    async fxRate(id) { const f = this.s.fx.find((x) => x.id === id); return f ? this.toFx(f) : null; }
    async saveFxRate(i, meta) {
        const dup = this.s.fx.find((f) => f.base === i.base && f.currency === i.currency && f.month === i.month && f.id !== i.id);
        if (dup)
            return { ok: false, duplicateOf: dup.id };
        const by = this.s.users.find((u) => u.id === meta.userId)?.name ?? '';
        const existing = i.id === null ? undefined : this.s.fx.find((f) => f.id === i.id);
        if (existing) {
            if (existing.rate !== i.rate)
                this.log('fxrate', existing.id, 'U', 'Rate', String(existing.rate), String(i.rate), meta);
            if (existing.currency !== i.currency)
                this.log('fxrate', existing.id, 'U', 'CurrencyCode', existing.currency, i.currency, meta);
            if (existing.month !== i.month)
                this.log('fxrate', existing.id, 'U', 'RateMonth', existing.month, i.month, meta);
            Object.assign(existing, { currency: i.currency, month: i.month, rate: i.rate, modifiedByName: by, modifiedAt: new Date().toISOString() });
            return { ok: true, id: existing.id };
        }
        const id = this.nextFx + this.s.fx.reduce((m, f) => Math.max(m, f.id), 0);
        this.s.fx.push({ id, base: i.base, currency: i.currency, month: i.month, rate: i.rate, modifiedByName: by, modifiedAt: new Date().toISOString() });
        this.log('fxrate', id, 'C', null, null, `${i.currency} ${i.month} ${i.rate}`, meta);
        return { ok: true, id };
    }
    async deleteFxRate(id, meta) {
        const i = this.s.fx.findIndex((f) => f.id === id);
        const f = this.s.fx[i];
        if (!f)
            return false;
        this.s.fx.splice(i, 1);
        this.log('fxrate', id, 'D', null, `${f.currency} ${f.month} ${f.rate}`, null, meta);
        return true;
    }
    async currencyUsage() {
        const out = {};
        for (const o of this.s.data.opportunities)
            out[String(o.CurrencyCode)] = (out[String(o.CurrencyCode)] ?? 0) + 1;
        return out;
    }
    async stageUsage() {
        return this.s.stages.map((st) => ({ ...st, used: this.s.data.opportunities.filter((o) => o.StageId === st.id).length })).sort((a, b) => a.sortOrder - b.sortOrder);
    }
    async saveStages(rows, meta) {
        const ids = rows.filter((r) => r.id !== null).map((r) => r.id);
        if (new Set(ids).size !== ids.length || ids.some((id) => !this.s.stages.some((o) => o.id === id)))
            return { ok: false, error: 'The stage list is out of date. Reload the page and try again.' };
        const keep = new Set(rows.filter((r) => r.id !== null).map((r) => r.id));
        for (const st of this.s.stages) {
            if (keep.has(st.id))
                continue;
            const used = this.s.data.opportunities.filter((o) => o.StageId === st.id).length;
            if (used > 0)
                return { ok: false, error: `Move ${used} ${used === 1 ? 'opportunity' : 'opportunities'} out of "${st.name}" before deleting it.` };
        }
        const next = rows.map((r, i) => ({ id: r.id ?? Math.max(0, ...this.s.stages.map((x) => x.id), ...rows.map((x) => x.id ?? 0)) + 1 + i, name: r.name, sortOrder: i + 1, probability: r.probability, type: r.type }));
        for (const st of this.s.stages)
            if (!keep.has(st.id))
                this.log('stage', st.id, 'D', null, st.name, null, meta);
        for (const n of next) {
            const o = this.s.stages.find((x) => x.id === n.id);
            if (!o) {
                this.log('stage', n.id, 'C', null, null, n.name, meta);
                continue;
            }
            for (const [f, a, b] of [['Name', o.name, n.name], ['Probability', o.probability, n.probability], ['StageType', o.type, n.type], ['SortOrder', o.sortOrder, n.sortOrder]]) {
                if (a !== b)
                    this.log('stage', n.id, 'U', f, String(a), String(b), meta);
            }
        }
        this.s.stages.splice(0, this.s.stages.length, ...next);
        return { ok: true };
    }
    list(code) { return PICKLISTS[code] ? (this.s.picklists[code] ??= []) : null; }
    async picklist(code) { const v = this.list(code); return v ? { name: PICKLISTS[code], values: v.map((x) => ({ ...x })) } : null; }
    async addPicklistValue(code, label, meta) {
        const v = this.list(code);
        if (!v || code === 'activity_type')
            return { ok: false, error: 'Values cannot be added to this list.' };
        if (v.some((x) => x.label.toLowerCase() === label.toLowerCase()))
            return { ok: false, error: `"${label}" already exists in this list.`, field: 'label' };
        const id = 1000 + this.s.nextId++;
        v.push({ id, code: null, label, active: true });
        this.log('picklistvalue', id, 'C', null, null, label, meta);
        return { ok: true };
    }
    async renamePicklistValue(code, id, label, meta) {
        const v = this.list(code);
        const x = v?.find((p) => p.id === id);
        if (!v || !x)
            return { ok: false, error: 'Value not found.' };
        if (v.some((p) => p.id !== id && p.label.toLowerCase() === label.toLowerCase()))
            return { ok: false, error: `"${label}" already exists in this list.`, field: 'label' };
        if (x.label !== label) {
            this.log('picklistvalue', id, 'U', 'Label', x.label, label, meta);
            x.label = label;
        }
        return { ok: true };
    }
    async setPicklistActive(code, id, active, meta) {
        const x = this.list(code)?.find((p) => p.id === id);
        if (!x)
            return { ok: false, error: 'Value not found.' };
        if (x.active !== active) {
            this.log('picklistvalue', id, 'U', 'IsActive', x.active ? '1' : '0', active ? '1' : '0', meta);
            x.active = active;
        }
        return { ok: true };
    }
    async movePicklistValue(code, id, dir, meta) {
        const v = this.list(code);
        const i = v?.findIndex((p) => p.id === id) ?? -1;
        const j = dir === 'up' ? i - 1 : i + 1;
        if (!v || i < 0)
            return { ok: false, error: 'Value not found.' };
        if (j < 0 || j >= v.length)
            return { ok: true };
        [v[i], v[j]] = [v[j], v[i]];
        this.log('picklistvalue', id, 'U', 'SortOrder', String(i + 1), String(j + 1), meta);
        return { ok: true };
    }
    members = (id) => this.s.users.filter((u) => u.teamId === id).length;
    toTeam = (t) => ({ id: t.id, name: t.name, groupId: t.groupId, active: t.active, members: this.members(t.id) });
    async teams() { return this.s.teams.map(this.toTeam).sort((a, b) => a.name.localeCompare(b.name)); }
    async team(id) { const t = this.s.teams.find((x) => x.id === id); return t ? this.toTeam(t) : null; }
    async saveTeam(i, meta) {
        const nameDup = this.s.teams.find((t) => t.id !== i.id && t.name.toLowerCase() === i.name.toLowerCase());
        if (nameDup)
            return { ok: false, field: 'name', error: `A team named ${nameDup.name} already exists.` };
        const groupDup = this.s.teams.find((t) => t.id !== i.id && t.groupId === i.groupId);
        if (groupDup)
            return { ok: false, field: 'groupId', error: `This group is already linked to the team ${groupDup.name}.` };
        const t = i.id === null ? undefined : this.s.teams.find((x) => x.id === i.id);
        if (i.id !== null && !t)
            return { ok: false, error: 'Team not found.' };
        if (t) {
            if (t.name !== i.name)
                this.log('team', t.id, 'U', 'Name', t.name, i.name, meta);
            if (t.groupId !== i.groupId)
                this.log('team', t.id, 'U', 'EntraGroupId', t.groupId, i.groupId, meta);
            t.name = i.name;
            t.groupId = i.groupId;
            return { ok: true, id: t.id };
        }
        const id = this.s.teams.reduce((m, x) => Math.max(m, x.id), 0) + 1;
        this.s.teams.push({ id, name: i.name, groupId: i.groupId, active: true });
        this.log('team', id, 'C', null, null, i.name, meta);
        return { ok: true, id };
    }
    async setTeamActive(id, active, meta) {
        const t = this.s.teams.find((x) => x.id === id);
        if (!t)
            return false;
        if (t.active !== active) {
            this.log('team', id, 'U', 'IsActive', t.active ? '1' : '0', active ? '1' : '0', meta);
            t.active = active;
        }
        return true;
    }
    async users() {
        return this.s.users.map((u) => ({ id: u.id, name: u.name, email: u.email ?? `${u.name.split(' ')[0]?.toLowerCase()}@demo.invalid`, role: u.role, teamId: u.teamId, teamName: this.s.teams.find((t) => t.id === u.teamId)?.name ?? null, teamStatus: u.teamStatus ?? (u.teamId ? 'ok' : 'none'), lastSignIn: u.lastSignIn ?? null, active: u.active }));
    }
}
exports.MemoryAdminRepo = MemoryAdminRepo;
//# sourceMappingURL=memoryAdmin.js.map