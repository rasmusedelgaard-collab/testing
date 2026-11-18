import { ConflictError, ForbiddenError } from '../errors';
import { ENTITIES } from '../domain/entities';
import type { AggRow, CrmStore, EntityKey, FxRateRow, HistoryRow, OppAggFilter, ListQuery, Lookups, Meta, OwnerOption, PicklistValue, RefOption, Repo, Row, Settings, Stage, UpdateResult, Value, Values } from '../domain/types';
import { createRow, deleteRow, updateRows, type AuditRow } from '../services/audit';
import type { RlsContext } from './withRlsContext';
import type { FxRate, LogoInfo } from '../domain/adminTypes';
import { MemoryAdminRepo } from './memoryAdmin';
import type { BrandingProvider } from '../services/brandingTypes';

/**
 * In-memory CrmStore for demo mode and tests. Emulates the RLS rules of overview 5.2 (visibility, update,
 * delete, resulting-row) so UI behaviour and authorization paths can be exercised without SQL Server.
 * It is NOT a security boundary; the database is.
 */
export interface MUser { id: number; name: string; role: 'Admin' | 'Manager' | 'User'; teamId: number | null; active: boolean; email?: string | null; lastSignIn?: string | null; teamStatus?: 'ok' | 'none' | 'multiple' | 'overage' }
type Raw = Row & { Id: number; Ver: number };

export interface MemoryState {
  users: MUser[];
  teams: Array<{ id: number; name: string; groupId: string; active: boolean }>;
  fx: FxRate[];
  logo: { contentType: LogoInfo['contentType']; content: Buffer; sha256: string } | null;
  settings: Settings;
  stages: Stage[];
  picklists: Record<string, PicklistValue[]>;
  data: Record<EntityKey, Raw[]>;
  audit: Array<AuditRow & { changedBy: number; correlationId: string; at: string }>;
  nextId: number;
}

export const iso = () => new Date().toISOString();
const hex = (n: number) => `0x${n.toString(16).padStart(16, '0')}`;

export function seedState(): MemoryState {
  const pl = (labels: string[], base: number): PicklistValue[] => labels.map((label, i) => ({ id: base + i, code: null, label, active: true }));
  const user = (id: number, name: string, role: MUser['role'], teamId: number | null): MUser => ({ id, name, role, teamId, active: true });
  const common = (owner: number, team: number | null) => ({ OwnerId: owner, TeamId: team, CreatedBy: owner, ModifiedBy: owner, CreatedAt: '2026-11-01T09:00:00.000Z', ModifiedAt: '2026-11-01T09:00:00.000Z', Ver: 1 });
  const s: MemoryState = {
    users: [user(1, 'Demo Admin', 'Admin', 1), user(2, 'Demo Manager', 'Manager', 1), user(3, 'Demo User', 'User', 1), user(4, 'Anna Teammate', 'User', 1), user(5, 'Otto Otherteam', 'User', 2), user(6, 'Nina NoTeam', 'User', null)],
    teams: [{ id: 1, name: 'Sales North', groupId: '3f2b9c1e-7a4d-4e8b-9c0f-1a2b3c4d5e6f', active: true }, { id: 2, name: 'Sales South', groupId: '9d8e7f6a-5b4c-4d3e-8f2a-0b1c2d3e4f5a', active: true }],
    fx: [],
    logo: null,
    settings: { appName: 'CRM', localeCode: 'da-DK', timezone: 'Europe/Copenhagen', baseCurrency: 'DKK', enabledCurrencies: ['DKK', 'EUR', 'USD', 'SEK', 'NOK'], visibilityMode: 'private', accentColor: '#1D4ED8', firstDayOfWeek: 1, fiscalStartMonth: 1 },
    stages: [
      { id: 1, name: 'Qualification', sortOrder: 1, probability: 10, type: 'open' }, { id: 2, name: 'Needs analysis', sortOrder: 2, probability: 25, type: 'open' },
      { id: 3, name: 'Proposal', sortOrder: 3, probability: 50, type: 'open' }, { id: 4, name: 'Negotiation', sortOrder: 4, probability: 75, type: 'open' },
      { id: 5, name: 'Closed won', sortOrder: 5, probability: 100, type: 'won' }, { id: 6, name: 'Closed lost', sortOrder: 6, probability: 0, type: 'lost' },
    ],
    picklists: {
      industry: pl(['Manufacturing', 'Technology', 'Finance', 'Healthcare', 'Retail', 'Services', 'Other'], 100),
      lead_source: pl(['Website', 'Referral', 'Event', 'Outbound', 'Partner'], 200),
      lost_reason: pl(['Price', 'Competitor', 'No budget', 'Timing'], 300),
    },
    data: { accounts: [], contacts: [], opportunities: [], activities: [] },
    audit: [],
    nextId: 1000,
  };
  const acc = (Id: number, Name: string, owner: number, team: number | null, City: string): Raw => ({ Id, Name, Website: null, Phone: null, IndustryValueId: 100, Street: null, PostalCode: null, City, CountryCode: 'DK', Notes: null, ...common(owner, team) });
  s.data.accounts.push(acc(1, 'Acme A/S', 3, 1, 'Aarhus'), acc(2, 'Nordic Tools ApS', 4, 1, 'Odense'), acc(3, 'Southern Foods', 5, 2, 'Malmö'), acc(4, 'Solo Consulting', 6, null, 'Aalborg'), acc(5, 'Manager Metals', 2, 1, 'Esbjerg'));
  const con = (Id: number, AccountId: number | null, FirstName: string, LastName: string, owner: number, team: number | null): Raw => ({ Id, AccountId, FirstName, LastName, JobTitle: 'Buyer', Email: `${LastName.toLowerCase()}@example.com`, Phone: null, Mobile: null, ...common(owner, team) });
  s.data.contacts.push(con(1, 1, 'Jane', 'Doe', 3, 1), con(2, 2, 'Ole', 'Hansen', 4, 1), con(3, 3, 'Sofia', 'Lind', 5, 2));
  const opp = (Id: number, Name: string, AccountId: number, StageId: number, Amount: number, CurrencyCode: string, CloseDate: string, owner: number, team: number | null): Raw =>
    ({ Id, Name, AccountId, PrimaryContactId: null, StageId, Amount, CurrencyCode, CloseDate, LeadSourceValueId: 200, LostReasonValueId: null, ClosedAt: null, ...common(owner, team) });
  s.data.opportunities.push(opp(1, 'Renewal 2027', 1, 3, 120000, 'DKK', '2026-12-15', 3, 1), opp(2, 'Tool rollout', 2, 2, 45000, 'EUR', '2027-01-20', 4, 1), opp(3, 'Southern deal', 3, 4, 80000, 'SEK', '2026-12-01', 5, 2));
  const act = (Id: number, ActivityType: string, Subject: string, AccountId: number | null, DueDate: string | null, owner: number, team: number | null): Raw =>
    ({ Id, ActivityType, Subject, Description: null, AccountId, ContactId: null, OpportunityId: null, DueDate, DueTime: null, IsDone: false, DoneAt: null, ...common(owner, team) });
  s.data.activities.push(act(1, 'call', 'Call Acme', 1, '2026-11-19', 3, 1), act(2, 'meeting', 'Kick-off Nordic Tools', 2, '2026-11-25', 4, 1), act(3, 'task', 'Send quote', 3, '2026-11-30', 5, 2));
  return s;
}

export function memoryBranding(state: MemoryState): BrandingProvider {
  return {
    get: async () => ({ appName: state.settings.appName, accentColor: state.settings.accentColor, logoSha: state.logo?.sha256 ?? null }),
    logo: async () => state.logo,
    invalidate: () => undefined,
  };
}

export function createMemoryStore(state: MemoryState = seedState()): CrmStore & { state: MemoryState } {
  return {
    state,
    run: async (ctx, fn) => fn(new MemoryRepo(state, ctx)),
    runAdmin: async (ctx, fn) => {
      if (ctx.role !== 'Admin') throw new ForbiddenError(); // emulates fn_AdminWrite / route check
      return fn(new MemoryAdminRepo(state, ctx));
    },
  };
}

export class MemoryRepo implements Repo {
  constructor(private s: MemoryState, private ctx: RlsContext) {}

  private get team() { return this.ctx.teamId; }
  private sameTeam(row: Row) { return this.team !== null && row.TeamId === this.team; }
  private canSee(row: Row): boolean {
    const c = this.ctx;
    return c.role === 'Admin' || row.OwnerId === c.userId || (this.sameTeam(row) && (c.role === 'Manager' || this.s.settings.visibilityMode === 'team'));
  }
  private canDelete(row: Row): boolean {
    const c = this.ctx;
    return c.role === 'Admin' || row.OwnerId === c.userId || (c.role === 'Manager' && this.sameTeam(row));
  }
  private ownerAllowed(ownerId: number): boolean {
    if (this.ctx.role === 'Admin') return true;
    if (ownerId === this.ctx.userId) return true;
    const u = this.s.users.find((x) => x.id === ownerId);
    return !!u && u.active && this.team !== null && u.teamId === this.team;
  }
  private user(id: unknown) { return this.s.users.find((u) => u.id === id); }

  async lookups(): Promise<Lookups> {
    return { settings: this.s.settings, stages: this.s.stages, picklists: this.s.picklists };
  }
  private toOwner = (u: MUser): OwnerOption => ({ id: u.id, name: u.name, teamName: this.s.teams.find((t) => t.id === u.teamId)?.name ?? null, active: u.active });
  async owners() {
    return this.s.users.filter((u) => (this.ctx.role === 'Admin' ? u.active : u.id === this.ctx.userId || (u.active && this.team !== null && u.teamId === this.team))).map(this.toOwner).sort((a, b) => a.name.localeCompare(b.name));
  }
  async ownerById(id: number) { const u = this.user(id); return u ? this.toOwner(u) : null; }

  private name = (c: Raw | undefined) => (c ? [c.FirstName, c.LastName].filter(Boolean).join(' ') : null);
  private decorate(entity: EntityKey, r: Raw): Row {
    const out: Row = { ...r, RowVer: hex(r.Ver), OwnerName: this.user(r.OwnerId)?.name ?? null, CreatedByName: this.user(r.CreatedBy)?.name ?? null, ModifiedByName: this.user(r.ModifiedBy)?.name ?? null };
    const find = (e: EntityKey, id: Value) => this.s.data[e].find((x) => x.Id === id && this.canSee(x));
    const label = (code: string, id: Value) => this.s.picklists[code]?.find((p) => p.id === id)?.label ?? null;
    if (entity === 'accounts') out.IndustryLabel = label('industry', r.IndustryValueId!);
    if (entity === 'contacts') out.AccountName = (find('accounts', r.AccountId!)?.Name as string) ?? null;
    if (entity === 'opportunities') {
      const st = this.s.stages.find((x) => x.id === r.StageId);
      Object.assign(out, { AccountName: find('accounts', r.AccountId!)?.Name ?? null, PrimaryContactName: this.name(find('contacts', r.PrimaryContactId!)), StageName: st?.name, StageType: st?.type, Probability: st?.probability, StageSort: st?.sortOrder, LeadSourceLabel: label('lead_source', r.LeadSourceValueId!), LostReasonLabel: label('lost_reason', r.LostReasonValueId!) });
    }
    if (entity === 'activities') Object.assign(out, { AccountName: find('accounts', r.AccountId!)?.Name ?? null, ContactName: this.name(find('contacts', r.ContactId!)), OpportunityName: find('opportunities', r.OpportunityId!)?.Name ?? null });
    return out;
  }

  async refOptions(entity: EntityKey): Promise<RefOption[]> {
    return this.s.data[entity].filter((r) => this.canSee(r)).map((r) => ({ id: r.Id, label: entity === 'contacts' ? (this.name(r) ?? '') : String(r.Name ?? r.Subject) })).sort((a, b) => a.label.localeCompare(b.label)).slice(0, 500);
  }

  private matches(entity: EntityKey, r: Row, q: ListQuery): boolean {
    const d = ENTITIES[entity];
    if (q.q && !d.search.some((s) => String(r[s.field] ?? '').toLowerCase().includes(q.q.toLowerCase()))) return false;
    for (const [k, v] of Object.entries(q.filters)) {
      const n = Number(v);
      switch (`${entity}.${k}`) {
        case 'accounts.owner': case 'contacts.owner': case 'opportunities.owner': case 'activities.owner': if (r.OwnerId !== n) return false; break;
        case 'accounts.industry': if (r.IndustryValueId !== n) return false; break;
        case 'contacts.account': case 'opportunities.account': case 'activities.account': if (r.AccountId !== n) return false; break;
        case 'opportunities.stage': if (r.StageId !== n) return false; break;
        case 'opportunities.contact': if (r.PrimaryContactId !== n) return false; break;
        case 'opportunities.from': if (String(r.CloseDate) < v) return false; break;
        case 'opportunities.to': if (String(r.CloseDate) > v) return false; break;
        case 'opportunities.status': if (v !== 'all' && (v === 'closed' ? r.StageType === 'open' : r.StageType !== v)) return false; break;
        case 'activities.type': if (v === 'actionable' ? r.ActivityType === 'note' : r.ActivityType !== v) return false; break;
        case 'activities.contact': if (r.ContactId !== n) return false; break;
        case 'activities.opportunity': if (r.OpportunityId !== n) return false; break;
        case 'activities.status': if ((v === 'open' && r.IsDone) || (v === 'done' && !r.IsDone)) return false; break;
        case 'activities.due': {
          const due = r.DueDate as string | null;
          if (v === 'overdue' && (r.IsDone || !due || due >= q.today)) return false;
          if (v === 'due' && (r.IsDone || !due || due > q.today)) return false;
          if (v === 'today' && due !== q.today) return false;
          if (v === 'week') { const end = new Date(`${q.today}T00:00:00Z`); end.setUTCDate(end.getUTCDate() + 6); if (!due || due < q.today || due > end.toISOString().slice(0, 10)) return false; }
          break;
        }
      }
    }
    return true;
  }

  async list(entity: EntityKey, q: ListQuery) {
    const d = ENTITIES[entity];
    const all = this.s.data[entity].filter((r) => this.canSee(r)).map((r) => this.decorate(entity, r)).filter((r) => this.matches(entity, r, q));
    const field = ((Object.hasOwn(d.sorts, q.sort) ? d.sorts[q.sort] : undefined) ?? d.sorts[d.defaultSort]!).field;
    const sign = q.dir === 'desc' ? -1 : 1;
    all.sort((a, b) => {
      const x = a[field] ?? null, y = b[field] ?? null;
      const c = x === y ? 0 : x === null ? -1 : y === null ? 1 : typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y));
      return (c || Number(a.Id) - Number(b.Id)) * sign;
    });
    return { total: all.length, rows: all.slice((q.page - 1) * q.pageSize, q.page * q.pageSize) };
  }

  async get(entity: EntityKey, id: number) {
    const r = this.s.data[entity].find((x) => x.Id === id);
    return r && this.canSee(r) ? this.decorate(entity, r) : null;
  }
  async refExists(entity: EntityKey, id: number) { return this.s.data[entity].some((x) => x.Id === id && this.canSee(x)); }

  async history(entity: EntityKey, id: number): Promise<HistoryRow[]> {
    const type = ENTITIES[entity].auditType;
    const owner = (f: string | null, v: string | null) => (f === 'OwnerId' && v ? (this.user(Number(v))?.name ?? v) : v);
    return this.s.audit.filter((a) => a.entityType === type && a.entityId === id).reverse()
      .map((a) => ({ at: a.at, by: this.user(a.changedBy)?.name ?? '', action: a.action, field: a.fieldName, oldValue: owner(a.fieldName, a.oldValue), newValue: owner(a.fieldName, a.newValue) }));
  }

  async oppAggregates(f: OppAggFilter): Promise<AggRow[]> {
    const groups = new Map<string, AggRow>();
    for (const raw of this.s.data.opportunities) {
      if (!this.canSee(raw)) continue;
      const o = this.decorate('opportunities', raw);
      const type = o.StageType as AggRow['stageType'];
      const close = String(o.CloseDate);
      if (!f.types.includes(type) || (f.from && close < f.from) || (f.to && close > f.to) || (f.ownerId && o.OwnerId !== f.ownerId) || (f.accountIds && !f.accountIds.includes(Number(o.AccountId)))) continue;
      const g = f.groupBy;
      const row: AggRow = {
        stageType: type, stageId: g.includes('stage') ? Number(o.StageId) : null, ownerId: g.includes('owner') ? Number(o.OwnerId) : null, ownerName: g.includes('owner') ? String(o.OwnerName) : null,
        leadSource: g.includes('leadSource') ? ((o.LeadSourceLabel as string | null) ?? null) : null, month: g.includes('month') ? close.slice(0, 7) : null, accountId: g.includes('account') ? Number(o.AccountId) : null,
        currency: String(o.CurrencyCode), count: 0, amount: 0,
      };
      const key = JSON.stringify([row.stageType, row.stageId, row.ownerId, row.leadSource, row.month, row.accountId, row.currency]);
      const cur = groups.get(key) ?? row;
      cur.count += 1;
      cur.amount += Number(o.Amount);
      groups.set(key, cur);
    }
    return [...groups.values()];
  }
  async fxRates(base: string): Promise<FxRateRow[]> {
    return this.s.fx.filter((f) => f.base === base).map((f) => ({ currency: f.currency, month: f.month, rate: f.rate }));
  }
  async activityCounts(today: string) {
    const end = new Date(`${today}T00:00:00Z`);
    end.setUTCDate(end.getUTCDate() + 7);
    const endStr = end.toISOString().slice(0, 10);
    const rows = this.s.data.activities.filter((a) => this.canSee(a) && !a.IsDone && a.ActivityType !== 'note' && a.DueDate);
    const due = (a: Raw) => String(a.DueDate);
    return { overdue: rows.filter((a) => due(a) < today).length, today: rows.filter((a) => due(a) === today).length, next7: rows.filter((a) => due(a) > today && due(a) <= endStr).length };
  }
  async activitiesByType(range: { from: string; to: string }, ownerId?: number) {
    const counts = new Map<string, number>();
    for (const a of this.s.data.activities) {
      if (!this.canSee(a) || (ownerId && a.OwnerId !== ownerId)) continue;
      const d = (a.DueDate as string | null) ?? String(a.CreatedAt).slice(0, 10);
      if (d < range.from || d > range.to) continue;
      counts.set(String(a.ActivityType), (counts.get(String(a.ActivityType)) ?? 0) + 1);
    }
    return [...counts].map(([type, count]) => ({ type, count }));
  }

  private teamOf(ownerId: unknown) { return this.user(ownerId)?.teamId ?? null; }
  private checkTarget(ownerId: unknown) {
    if (this.ctx.role !== 'Admin' && (!this.ownerAllowed(Number(ownerId)) || this.teamOf(ownerId) !== this.team)) throw new ForbiddenError();
  }
  private audit(rows: AuditRow[], meta: Meta) { for (const a of rows) this.s.audit.push({ ...a, changedBy: meta.userId, correlationId: meta.correlationId, at: iso() }); }
  private label(entity: EntityKey, r: Row) { return String(r[ENTITIES[entity].labelCol] ?? ''); }

  async create(entity: EntityKey, values: Values, meta: Meta) {
    this.checkTarget(values.OwnerId);
    const id = this.s.nextId++;
    const now = iso();
    const r: Raw = { ...values, Id: id, TeamId: this.teamOf(values.OwnerId), CreatedBy: meta.userId, ModifiedBy: meta.userId, CreatedAt: now, ModifiedAt: now, Ver: 1 };
    this.fixDerived(entity, r, null);
    this.s.data[entity].push(r);
    this.audit([createRow(entity, id, this.label(entity, r), Number(r.OwnerId), r.TeamId as number | null)], meta);
    return id;
  }

  private fixDerived(entity: EntityKey, r: Raw, old: Raw | null) {
    if (entity === 'opportunities') r.ClosedAt = ['won', 'lost'].includes(this.s.stages.find((x) => x.id === r.StageId)?.type ?? '') ? (old?.ClosedAt ?? iso()) : null;
    if (entity === 'activities') r.DoneAt = r.IsDone ? (old?.DoneAt ?? iso()) : null;
  }

  async update(entity: EntityKey, id: number, values: Values, rowVer: string, meta: Meta): Promise<UpdateResult> {
    const old = this.s.data[entity].find((x) => x.Id === id);
    if (!old || !this.canSee(old)) return 'notfound';
    if (hex(old.Ver) !== rowVer) return 'conflict';
    this.checkTarget(values.OwnerId);
    const before = { ...old };
    Object.assign(old, values, { TeamId: this.teamOf(values.OwnerId), ModifiedBy: meta.userId, ModifiedAt: iso(), Ver: old.Ver + 1 });
    this.fixDerived(entity, old, before);
    const stageName = (f: string, v: Value) => (f === 'StageId' ? (this.s.stages.find((s) => s.id === v)?.name ?? v) : v);
    this.audit(updateRows(entity, id, before, old, Number(old.OwnerId), old.TeamId as number | null, stageName), meta);
    return 'ok';
  }

  async remove(entity: EntityKey, id: number, meta: Meta) {
    const arr = this.s.data[entity];
    const i = arr.findIndex((x) => x.Id === id);
    const r = arr[i];
    if (!r || !this.canSee(r)) return false;
    if (!this.canDelete(r)) throw new ForbiddenError();
    if (entity === 'accounts' && this.s.data.opportunities.some((o) => o.AccountId === id)) {
      throw new ConflictError('Record is still linked to other records.');
    }
    const unlink = (e: EntityKey, col: string) => this.s.data[e].forEach((x) => { if (x[col] === id) x[col] = null; });
    if (entity === 'accounts') { unlink('contacts', 'AccountId'); unlink('activities', 'AccountId'); }
    if (entity === 'contacts') { unlink('opportunities', 'PrimaryContactId'); unlink('activities', 'ContactId'); }
    if (entity === 'opportunities') unlink('activities', 'OpportunityId');
    arr.splice(i, 1);
    this.audit([deleteRow(entity, id, this.label(entity, r), Number(r.OwnerId), r.TeamId as number | null)], meta);
    return true;
  }

  async setDone(id: number, done: boolean, meta: Meta) {
    const r = this.s.data.activities.find((x) => x.Id === id);
    if (!r || !this.canSee(r)) return false;
    Object.assign(r, { IsDone: done, DoneAt: done ? iso() : null, ModifiedBy: meta.userId, ModifiedAt: iso(), Ver: r.Ver + 1 });
    return true;
  }
}
