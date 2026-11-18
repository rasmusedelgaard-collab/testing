import type { RlsContext } from '../db/withRlsContext';

export type EntityKey = 'accounts' | 'contacts' | 'opportunities' | 'activities';
export type Value = string | number | boolean | null;
/** A record as the UI sees it: raw columns plus joined display names. Dates are 'YYYY-MM-DD', timestamps ISO strings. */
export type Row = Record<string, Value>;
export type Values = Record<string, Value>;

export interface Settings {
  appName: string;
  localeCode: string;
  timezone: string;
  baseCurrency: string;
  enabledCurrencies: string[];
  visibilityMode: 'private' | 'team';
  accentColor: string;
  firstDayOfWeek: number;
  fiscalStartMonth: number;
}
export interface Stage { id: number; name: string; sortOrder: number; probability: number; type: 'open' | 'won' | 'lost' }
export interface PicklistValue { id: number; code: string | null; label: string; active: boolean }
export interface Lookups { settings: Settings; stages: Stage[]; picklists: Record<string, PicklistValue[]> }
export interface OwnerOption { id: number; name: string; teamName: string | null; active: boolean }
export interface RefOption { id: number; label: string }

export interface ListQuery {
  q: string;
  page: number;
  pageSize: number;
  sort: string;
  dir: 'asc' | 'desc';
  filters: Record<string, string>;
  /** Today in the Settings timezone, YYYY-MM-DD (for due-date filters). */
  today: string;
}
export interface HistoryRow { at: string; by: string; action: 'C' | 'U' | 'D'; field: string | null; oldValue: string | null; newValue: string | null }
export interface FxRateRow { currency: string; /** YYYY-MM */ month: string; rate: number }
/** One group of opportunities from the SQL aggregate (already RLS-filtered). Unused dimensions are null. */
export interface AggRow {
  stageType: 'open' | 'won' | 'lost';
  stageId: number | null;
  ownerId: number | null;
  ownerName: string | null;
  leadSource: string | null;
  month: string | null;
  accountId: number | null;
  currency: string;
  count: number;
  amount: number;
}
export interface OppAggFilter {
  types: Array<'open' | 'won' | 'lost'>;
  /** Close-date range, inclusive. */
  from?: string;
  to?: string;
  ownerId?: number;
  teamId?: number;
  accountIds?: number[];
  groupBy: Array<'stage' | 'owner' | 'leadSource' | 'month' | 'account'>;
}
export interface Meta { userId: number; correlationId: string }
export type UpdateResult = 'ok' | 'conflict' | 'notfound';

/** Everything a request needs from the database. One instance per request, bound to one transaction. */
export interface Repo {
  lookups(): Promise<Lookups>;
  owners(): Promise<OwnerOption[]>;
  ownerById(id: number): Promise<OwnerOption | null>;
  refOptions(entity: EntityKey): Promise<RefOption[]>;
  list(entity: EntityKey, q: ListQuery): Promise<{ rows: Row[]; total: number }>;
  get(entity: EntityKey, id: number): Promise<Row | null>;
  refExists(entity: EntityKey, id: number): Promise<boolean>;
  /** Audit trail of one record, newest first (owner ids already resolved to names). */
  history(entity: EntityKey, id: number): Promise<HistoryRow[]>;
  /** Grouped opportunity sums/counts for KPIs and charts. Amounts stay in their own currency (convert with services/fx). */
  oppAggregates(f: OppAggFilter): Promise<AggRow[]>;
  fxRates(base: string): Promise<FxRateRow[]>;
  /** Open Call/Meeting/Task activities that are overdue, due today, or due in the next 7 days (visible rows only). */
  activityCounts(today: string): Promise<{ overdue: number; today: number; next7: number }>;
  /** Activities per type with COALESCE(DueDate, created date) in the range. */
  activitiesByType(range: { from: string; to: string }, scope?: { ownerId?: number; teamId?: number }): Promise<Array<{ type: string; count: number }>>;
  /** Teams for the Analytics filter (team names are not sensitive; crm.Teams has no FILTER). */
  teams(): Promise<Array<{ id: number; name: string }>>;
  create(entity: EntityKey, values: Values, meta: Meta): Promise<number>;
  update(entity: EntityKey, id: number, values: Values, rowVer: string, meta: Meta): Promise<UpdateResult>;
  remove(entity: EntityKey, id: number, meta: Meta): Promise<boolean>;
  setDone(id: number, done: boolean, meta: Meta): Promise<boolean>;
}

export interface CrmStore {
  run<T>(ctx: RlsContext, fn: (repo: Repo) => Promise<T>): Promise<T>;
  /** Admin configuration (settings, stages, picklists, FX, teams). Same one-transaction-per-request rule. */
  runAdmin<T>(ctx: RlsContext, fn: (repo: import('./adminTypes').AdminRepo) => Promise<T>): Promise<T>;
}
