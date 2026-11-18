import type { Lookups, Meta, PicklistValue, Stage } from './types';

export interface FxRate { id: number; base: string; currency: string; /** YYYY-MM */ month: string; rate: number; modifiedByName: string; modifiedAt: string }
export interface StageInput { id: number | null; name: string; probability: number; type: 'open' | 'won' | 'lost' }
export interface StageUsage extends Stage { used: number }
export interface TeamRow { id: number; name: string; groupId: string; active: boolean; members: number }
export interface UserRow { id: number; name: string; email: string | null; role: string; teamId: number | null; teamName: string | null; teamStatus: 'ok' | 'none' | 'multiple' | 'overage'; lastSignIn: string | null; active: boolean }
export interface LogoInfo { contentType: 'image/png' | 'image/svg+xml'; sha256: string }
export type Outcome = { ok: true } | { ok: false; error: string; field?: string };

/** Admin configuration access. One instance per request, bound to one RLS transaction (Admin context). */
export interface AdminRepo {
  lookups(): Promise<Lookups>;
  settingsMap(): Promise<Record<string, string>>;
  saveSettings(updates: Record<string, string>, meta: Meta): Promise<void>;
  logoInfo(): Promise<LogoInfo | null>;
  saveLogo(contentType: LogoInfo['contentType'], content: Buffer, sha256: string, meta: Meta): Promise<void>;
  fxRates(): Promise<FxRate[]>;
  fxRate(id: number): Promise<FxRate | null>;
  /** Duplicate (base, currency, month) is reported, never overwritten. */
  saveFxRate(input: { id: number | null; base: string; currency: string; month: string; rate: number }, meta: Meta): Promise<{ ok: true; id: number } | { ok: false; duplicateOf: number }>;
  deleteFxRate(id: number, meta: Meta): Promise<boolean>;
  /** Opportunities per currency (Admin sees all rows). Used to warn before disabling a currency. */
  currencyUsage(): Promise<Record<string, number>>;
  stageUsage(): Promise<StageUsage[]>;
  saveStages(rows: StageInput[], meta: Meta): Promise<Outcome>;
  picklist(code: string): Promise<{ name: string; values: PicklistValue[] } | null>;
  addPicklistValue(code: string, label: string, meta: Meta): Promise<Outcome>;
  renamePicklistValue(code: string, id: number, label: string, meta: Meta): Promise<Outcome>;
  setPicklistActive(code: string, id: number, active: boolean, meta: Meta): Promise<Outcome>;
  movePicklistValue(code: string, id: number, dir: 'up' | 'down', meta: Meta): Promise<Outcome>;
  teams(): Promise<TeamRow[]>;
  team(id: number): Promise<TeamRow | null>;
  saveTeam(input: { id: number | null; name: string; groupId: string }, meta: Meta): Promise<Outcome & { id?: number }>;
  setTeamActive(id: number, active: boolean, meta: Meta): Promise<boolean>;
  users(): Promise<UserRow[]>;
}
