import type { AdminRepo, FxRate, LogoInfo, Outcome, StageInput, StageUsage, TeamRow, UserRow } from '../domain/adminTypes';
import type { Lookups, Meta, PicklistValue } from '../domain/types';
import type { AuditRow } from '../services/audit';
import type { MemoryState } from './memoryStore';
import type { RlsContext } from './withRlsContext';

const PICKLISTS: Record<string, string> = { industry: 'Industry', lead_source: 'Lead source', lost_reason: 'Lost reason', activity_type: 'Activity type' };
const KEYS: Array<[string, (s: MemoryState['settings']) => string, (s: MemoryState['settings'], v: string) => void]> = [
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
export class MemoryAdminRepo implements AdminRepo {
  private nextFx = 1;
  constructor(private s: MemoryState, private ctx: RlsContext) {}

  private log(type: string, id: number, action: 'C' | 'U' | 'D', field: string | null, o: string | null, n: string | null, meta: Meta) {
    const row: AuditRow = { entityType: type, entityId: id, action, fieldName: field, oldValue: o, newValue: n, ownerId: meta.userId, teamId: null };
    this.s.audit.push({ ...row, changedBy: meta.userId, correlationId: meta.correlationId, at: new Date().toISOString() });
  }

  async lookups(): Promise<Lookups> { return { settings: this.s.settings, stages: this.s.stages, picklists: this.s.picklists }; }
  async settingsMap() { return Object.fromEntries(KEYS.map(([k, get]) => [k, get(this.s.settings)])); }
  async saveSettings(updates: Record<string, string>, meta: Meta) {
    for (const [key, get, set] of KEYS) {
      if (!(key in updates)) continue;
      const old = get(this.s.settings);
      const nv = updates[key] as string;
      if (old === nv) continue;
      set(this.s.settings, nv);
      this.log('setting', KEYS.findIndex((k) => k[0] === key) + 1, 'U', key, old, nv, meta);
    }
  }
  async logoInfo(): Promise<LogoInfo | null> { return this.s.logo ? { contentType: this.s.logo.contentType, sha256: this.s.logo.sha256 } : null; }
  async saveLogo(contentType: LogoInfo['contentType'], content: Buffer, sha256: string, meta: Meta) {
    const old = this.s.logo?.sha256 ?? null;
    this.s.logo = { contentType, content, sha256 };
    this.log('branding', 1, old ? 'U' : 'C', 'logo', old, sha256, meta);
  }

  private toFx(f: FxRate) { return { ...f }; }
  async fxRates() { return this.s.fx.map(this.toFx).sort((a, b) => b.month.localeCompare(a.month) || a.currency.localeCompare(b.currency)); }
  async fxRate(id: number) { const f = this.s.fx.find((x) => x.id === id); return f ? this.toFx(f) : null; }
  async saveFxRate(i: { id: number | null; base: string; currency: string; month: string; rate: number }, meta: Meta) {
    const dup = this.s.fx.find((f) => f.base === i.base && f.currency === i.currency && f.month === i.month && f.id !== i.id);
    if (dup) return { ok: false as const, duplicateOf: dup.id };
    const by = this.s.users.find((u) => u.id === meta.userId)?.name ?? '';
    const existing = i.id === null ? undefined : this.s.fx.find((f) => f.id === i.id);
    if (existing) {
      if (existing.rate !== i.rate) this.log('fxrate', existing.id, 'U', 'Rate', String(existing.rate), String(i.rate), meta);
      if (existing.currency !== i.currency) this.log('fxrate', existing.id, 'U', 'CurrencyCode', existing.currency, i.currency, meta);
      if (existing.month !== i.month) this.log('fxrate', existing.id, 'U', 'RateMonth', existing.month, i.month, meta);
      Object.assign(existing, { currency: i.currency, month: i.month, rate: i.rate, modifiedByName: by, modifiedAt: new Date().toISOString() });
      return { ok: true as const, id: existing.id };
    }
    const id = this.nextFx + this.s.fx.reduce((m, f) => Math.max(m, f.id), 0);
    this.s.fx.push({ id, base: i.base, currency: i.currency, month: i.month, rate: i.rate, modifiedByName: by, modifiedAt: new Date().toISOString() });
    this.log('fxrate', id, 'C', null, null, `${i.currency} ${i.month} ${i.rate}`, meta);
    return { ok: true as const, id };
  }
  async deleteFxRate(id: number, meta: Meta) {
    const i = this.s.fx.findIndex((f) => f.id === id);
    const f = this.s.fx[i];
    if (!f) return false;
    this.s.fx.splice(i, 1);
    this.log('fxrate', id, 'D', null, `${f.currency} ${f.month} ${f.rate}`, null, meta);
    return true;
  }

  async stageUsage(): Promise<StageUsage[]> {
    return this.s.stages.map((st) => ({ ...st, used: this.s.data.opportunities.filter((o) => o.StageId === st.id).length })).sort((a, b) => a.sortOrder - b.sortOrder);
  }
  async saveStages(rows: StageInput[], meta: Meta): Promise<Outcome> {
    const keep = new Set(rows.filter((r) => r.id !== null).map((r) => r.id));
    for (const st of this.s.stages) {
      if (keep.has(st.id)) continue;
      const used = this.s.data.opportunities.filter((o) => o.StageId === st.id).length;
      if (used > 0) return { ok: false, error: `Move ${used} ${used === 1 ? 'opportunity' : 'opportunities'} out of "${st.name}" before deleting it.` };
    }
    const next = rows.map((r, i) => ({ id: r.id ?? Math.max(0, ...this.s.stages.map((x) => x.id), ...rows.map((x) => x.id ?? 0)) + 1 + i, name: r.name, sortOrder: i + 1, probability: r.probability, type: r.type }));
    for (const st of this.s.stages) if (!keep.has(st.id)) this.log('stage', st.id, 'D', null, st.name, null, meta);
    for (const n of next) {
      const o = this.s.stages.find((x) => x.id === n.id);
      if (!o) { this.log('stage', n.id, 'C', null, null, n.name, meta); continue; }
      for (const [f, a, b] of [['Name', o.name, n.name], ['Probability', o.probability, n.probability], ['StageType', o.type, n.type], ['SortOrder', o.sortOrder, n.sortOrder]] as const) {
        if (a !== b) this.log('stage', n.id, 'U', f, String(a), String(b), meta);
      }
    }
    this.s.stages.splice(0, this.s.stages.length, ...next);
    return { ok: true };
  }

  private list(code: string): PicklistValue[] | null { return PICKLISTS[code] ? (this.s.picklists[code] ??= []) : null; }
  async picklist(code: string) { const v = this.list(code); return v ? { name: PICKLISTS[code] as string, values: v.map((x) => ({ ...x })) } : null; }
  async addPicklistValue(code: string, label: string, meta: Meta): Promise<Outcome> {
    const v = this.list(code);
    if (!v || code === 'activity_type') return { ok: false, error: 'Values cannot be added to this list.' };
    if (v.some((x) => x.label.toLowerCase() === label.toLowerCase())) return { ok: false, error: `"${label}" already exists in this list.`, field: 'label' };
    const id = 1000 + this.s.nextId++;
    v.push({ id, code: null, label, active: true });
    this.log('picklistvalue', id, 'C', null, null, label, meta);
    return { ok: true };
  }
  async renamePicklistValue(code: string, id: number, label: string, meta: Meta): Promise<Outcome> {
    const v = this.list(code);
    const x = v?.find((p) => p.id === id);
    if (!v || !x) return { ok: false, error: 'Value not found.' };
    if (v.some((p) => p.id !== id && p.label.toLowerCase() === label.toLowerCase())) return { ok: false, error: `"${label}" already exists in this list.`, field: 'label' };
    if (x.label !== label) { this.log('picklistvalue', id, 'U', 'Label', x.label, label, meta); x.label = label; }
    return { ok: true };
  }
  async setPicklistActive(code: string, id: number, active: boolean, meta: Meta): Promise<Outcome> {
    const x = this.list(code)?.find((p) => p.id === id);
    if (!x) return { ok: false, error: 'Value not found.' };
    if (x.active !== active) { this.log('picklistvalue', id, 'U', 'IsActive', x.active ? '1' : '0', active ? '1' : '0', meta); x.active = active; }
    return { ok: true };
  }
  async movePicklistValue(code: string, id: number, dir: 'up' | 'down', meta: Meta): Promise<Outcome> {
    const v = this.list(code);
    const i = v?.findIndex((p) => p.id === id) ?? -1;
    const j = dir === 'up' ? i - 1 : i + 1;
    if (!v || i < 0) return { ok: false, error: 'Value not found.' };
    if (j < 0 || j >= v.length) return { ok: true };
    [v[i], v[j]] = [v[j]!, v[i]!];
    this.log('picklistvalue', id, 'U', 'SortOrder', String(i + 1), String(j + 1), meta);
    return { ok: true };
  }

  private members = (id: number) => this.s.users.filter((u) => u.teamId === id).length;
  private toTeam = (t: MemoryState['teams'][number]): TeamRow => ({ id: t.id, name: t.name, groupId: t.groupId, active: t.active, members: this.members(t.id) });
  async teams() { return this.s.teams.map(this.toTeam).sort((a, b) => a.name.localeCompare(b.name)); }
  async team(id: number) { const t = this.s.teams.find((x) => x.id === id); return t ? this.toTeam(t) : null; }
  async saveTeam(i: { id: number | null; name: string; groupId: string }, meta: Meta) {
    const nameDup = this.s.teams.find((t) => t.id !== i.id && t.name.toLowerCase() === i.name.toLowerCase());
    if (nameDup) return { ok: false as const, field: 'name', error: `A team named ${nameDup.name} already exists.` };
    const groupDup = this.s.teams.find((t) => t.id !== i.id && t.groupId === i.groupId);
    if (groupDup) return { ok: false as const, field: 'groupId', error: `This group is already linked to the team ${groupDup.name}.` };
    const t = i.id === null ? undefined : this.s.teams.find((x) => x.id === i.id);
    if (i.id !== null && !t) return { ok: false as const, error: 'Team not found.' };
    if (t) {
      if (t.name !== i.name) this.log('team', t.id, 'U', 'Name', t.name, i.name, meta);
      if (t.groupId !== i.groupId) this.log('team', t.id, 'U', 'EntraGroupId', t.groupId, i.groupId, meta);
      t.name = i.name; t.groupId = i.groupId;
      return { ok: true as const, id: t.id };
    }
    const id = this.s.teams.reduce((m, x) => Math.max(m, x.id), 0) + 1;
    this.s.teams.push({ id, name: i.name, groupId: i.groupId, active: true });
    this.log('team', id, 'C', null, null, i.name, meta);
    return { ok: true as const, id };
  }
  async setTeamActive(id: number, active: boolean, meta: Meta) {
    const t = this.s.teams.find((x) => x.id === id);
    if (!t) return false;
    if (t.active !== active) { this.log('team', id, 'U', 'IsActive', t.active ? '1' : '0', active ? '1' : '0', meta); t.active = active; }
    return true;
  }
  async users(): Promise<UserRow[]> {
    return this.s.users.map((u) => ({ id: u.id, name: u.name, email: u.email ?? `${u.name.split(' ')[0]?.toLowerCase()}@demo.invalid`, role: u.role, teamId: u.teamId, teamName: this.s.teams.find((t) => t.id === u.teamId)?.name ?? null, teamStatus: u.teamStatus ?? (u.teamId ? 'ok' : 'none'), lastSignIn: u.lastSignIn ?? null, active: u.active }));
  }
}
