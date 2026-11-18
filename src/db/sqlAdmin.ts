import sql from 'mssql';
import type { AdminRepo, FxRate, LogoInfo, Outcome, StageInput, StageUsage, TeamRow, UserRow } from '../domain/adminTypes';
import type { Lookups, Meta, PicklistValue, Row } from '../domain/types';
import { SqlRepo } from './sqlRepo';
import { serial } from './txQueue';
import type { RlsContext } from './withRlsContext';

type Param = [name: string, type: unknown, value: unknown];
const NUM = (v: unknown) => Number(v);

/** All Admin configuration SQL. Runs in the Admin's RLS transaction; fn_AdminWrite is the database-side backstop. */
export class SqlAdminRepo implements AdminRepo {
  private readonly base: SqlRepo;
  constructor(private readonly tx: sql.Transaction, private readonly ctx: RlsContext) { this.base = new SqlRepo(tx, ctx); }

  private async q(text: string, params: Param[] = []) {
    // Serialised per transaction: never two requests in flight on one connection.
    return serial(this.tx, () => {
      const r = new sql.Request(this.tx);
      for (const [n, t, v] of params) r.input(n, t as sql.ISqlType, v);
      return r.query(text);
    });
  }
  private rows(res: Awaited<ReturnType<SqlAdminRepo['q']>>, i = 0): Row[] {
    const sets = res.recordsets as unknown as Row[][];
    return sets[i] ?? (res.recordset as unknown as Row[]) ?? [];
  }
  /** Config audit rows: OwnerId = ChangedBy and TeamId NULL (overview 7). */
  private async audit(type: string, id: number, action: 'C' | 'U' | 'D', field: string | null, oldV: string | null, newV: string | null, meta: Meta) {
    await this.q(`INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId)
      VALUES (@et, @eid, @act, @fn, @ov, @nv, @by, NULL, @by, @cid)`, [
      ['et', sql.VarChar(20), type], ['eid', sql.Int, id], ['act', sql.Char(1), action], ['fn', sql.VarChar(64), field],
      ['ov', sql.NVarChar(4000), oldV?.slice(0, 4000) ?? null], ['nv', sql.NVarChar(4000), newV?.slice(0, 4000) ?? null], ['by', sql.Int, meta.userId], ['cid', sql.UniqueIdentifier, meta.correlationId]]);
  }

  lookups(): Promise<Lookups> { return this.base.lookups(); }

  async settingsMap() {
    const res = await this.q('SELECT SettingKey, SettingValue FROM crm.Settings');
    return Object.fromEntries(this.rows(res).map((r) => [String(r.SettingKey), String(r.SettingValue)]));
  }
  async saveSettings(updates: Record<string, string>, meta: Meta) {
    for (const [key, value] of Object.entries(updates)) {
      const cur = this.rows(await this.q('SELECT SettingId, SettingValue FROM crm.Settings WHERE SettingKey = @k', [['k', sql.VarChar(64), key]]))[0];
      if (!cur) {
        const ins = this.rows(await this.q('INSERT crm.Settings (SettingKey, SettingValue, ModifiedBy) OUTPUT inserted.SettingId AS Id VALUES (@k, @v, @by)', [['k', sql.VarChar(64), key], ['v', sql.NVarChar(1000), value], ['by', sql.Int, meta.userId]]))[0];
        await this.audit('setting', NUM(ins?.Id), 'C', key, null, value, meta);
      } else if (String(cur.SettingValue) !== value) {
        await this.q('UPDATE crm.Settings SET SettingValue = @v, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE SettingId = @id', [['v', sql.NVarChar(1000), value], ['by', sql.Int, meta.userId], ['id', sql.Int, cur.SettingId]]);
        await this.audit('setting', NUM(cur.SettingId), 'U', key, String(cur.SettingValue), value, meta);
      }
    }
  }

  async logoInfo(): Promise<LogoInfo | null> {
    const r = this.rows(await this.q(`SELECT ContentType, LOWER(CONVERT(char(64), Sha256, 2)) AS Sha FROM crm.BrandingAssets WHERE AssetKey = 'logo'`))[0];
    return r ? { contentType: r.ContentType as LogoInfo['contentType'], sha256: String(r.Sha) } : null;
  }
  async saveLogo(contentType: LogoInfo['contentType'], content: Buffer, sha256: string, meta: Meta) {
    const p: Param[] = [['ct', sql.VarChar(30), contentType], ['c', sql.VarBinary(sql.MAX), content], ['sha', sql.VarChar(64), sha256], ['by', sql.Int, meta.userId]];
    const upd = this.rows(await this.q(`UPDATE crm.BrandingAssets SET ContentType = @ct, Content = @c, Sha256 = CONVERT(binary(32), @sha, 2), ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME()
      OUTPUT inserted.AssetId AS Id WHERE AssetKey = 'logo'`, p))[0];
    if (upd) return this.audit('branding', NUM(upd.Id), 'U', 'logo', null, sha256, meta);
    const ins = this.rows(await this.q(`INSERT crm.BrandingAssets (AssetKey, ContentType, Content, Sha256, ModifiedBy) OUTPUT inserted.AssetId AS Id
      VALUES ('logo', @ct, @c, CONVERT(binary(32), @sha, 2), @by)`, p))[0];
    await this.audit('branding', NUM(ins?.Id), 'C', 'logo', null, sha256, meta);
  }

  private static readonly FX = `SELECT f.FxRateId, f.BaseCurrency, f.CurrencyCode, CONVERT(char(7), f.RateMonth, 23) AS Month, f.Rate, u.DisplayName AS ByName, CONVERT(varchar(23), f.ModifiedAt, 126) + 'Z' AS At
    FROM crm.FxRates f JOIN crm.Users u ON u.UserId = f.ModifiedBy`;
  private toFx = (r: Row): FxRate => ({ id: NUM(r.FxRateId), base: String(r.BaseCurrency).trim(), currency: String(r.CurrencyCode).trim(), month: String(r.Month), rate: NUM(r.Rate), modifiedByName: String(r.ByName), modifiedAt: String(r.At) });
  async fxRates() { return this.rows(await this.q(`${SqlAdminRepo.FX} ORDER BY f.RateMonth DESC, f.CurrencyCode`)).map(this.toFx); }
  async fxRate(id: number) { const r = this.rows(await this.q(`${SqlAdminRepo.FX} WHERE f.FxRateId = @id`, [['id', sql.Int, id]]))[0]; return r ? this.toFx(r) : null; }
  async saveFxRate(i: { id: number | null; base: string; currency: string; month: string; rate: number }, meta: Meta) {
    const dup = this.rows(await this.q(`SELECT FxRateId FROM crm.FxRates WHERE BaseCurrency = @b AND CurrencyCode = @c AND RateMonth = @m AND (@id IS NULL OR FxRateId <> @id)`,
      [['b', sql.Char(3), i.base], ['c', sql.Char(3), i.currency], ['m', sql.VarChar(10), `${i.month}-01`], ['id', sql.Int, i.id]]))[0];
    if (dup) return { ok: false as const, duplicateOf: NUM(dup.FxRateId) };
    const p: Param[] = [['b', sql.Char(3), i.base], ['c', sql.Char(3), i.currency], ['m', sql.VarChar(10), `${i.month}-01`], ['r', sql.Decimal(19, 8), i.rate], ['by', sql.Int, meta.userId]];
    if (i.id === null) {
      const ins = this.rows(await this.q('INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) OUTPUT inserted.FxRateId AS Id VALUES (@b, @c, @m, @r, @by)', p))[0];
      const id = NUM(ins?.Id);
      await this.audit('fxrate', id, 'C', null, null, `${i.currency} ${i.month} ${i.rate}`, meta);
      return { ok: true as const, id };
    }
    const old = this.rows(await this.q('SELECT CurrencyCode, CONVERT(char(7), RateMonth, 23) AS Month, Rate FROM crm.FxRates WHERE FxRateId = @id', [['id', sql.Int, i.id]]))[0];
    if (!old) return { ok: false as const, duplicateOf: 0 };
    await this.q('UPDATE crm.FxRates SET CurrencyCode = @c, RateMonth = @m, Rate = @r, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE FxRateId = @id', [...p, ['id', sql.Int, i.id]]);
    if (NUM(old.Rate) !== i.rate) await this.audit('fxrate', i.id, 'U', 'Rate', String(old.Rate), String(i.rate), meta);
    if (String(old.CurrencyCode).trim() !== i.currency) await this.audit('fxrate', i.id, 'U', 'CurrencyCode', String(old.CurrencyCode).trim(), i.currency, meta);
    if (String(old.Month) !== i.month) await this.audit('fxrate', i.id, 'U', 'RateMonth', String(old.Month), i.month, meta);
    return { ok: true as const, id: i.id };
  }
  async deleteFxRate(id: number, meta: Meta) {
    const r = this.rows(await this.q(`DELETE crm.FxRates OUTPUT deleted.CurrencyCode, CONVERT(char(7), deleted.RateMonth, 23) AS Month, deleted.Rate WHERE FxRateId = @id`, [['id', sql.Int, id]]))[0];
    if (!r) return false;
    await this.audit('fxrate', id, 'D', null, `${String(r.CurrencyCode).trim()} ${r.Month} ${r.Rate}`, null, meta);
    return true;
  }

  async currencyUsage(): Promise<Record<string, number>> {
    const res = await this.q('SELECT CurrencyCode, COUNT(*) AS Cnt FROM crm.Opportunities GROUP BY CurrencyCode');
    return Object.fromEntries(this.rows(res).map((r) => [String(r.CurrencyCode).trim(), NUM(r.Cnt)]));
  }

  async stageUsage(): Promise<StageUsage[]> {
    // Opportunities are counted under RLS: the app can only count what the Admin may see (Admin sees all).
    const res = await this.q(`SELECT s.StageId, s.Name, s.SortOrder, s.Probability, s.StageType, (SELECT COUNT(*) FROM crm.Opportunities o WHERE o.StageId = s.StageId) AS Used FROM crm.PipelineStages s ORDER BY s.SortOrder`);
    return this.rows(res).map((r) => ({ id: NUM(r.StageId), name: String(r.Name), sortOrder: NUM(r.SortOrder), probability: NUM(r.Probability), type: r.StageType as StageUsage['type'], used: NUM(r.Used) }));
  }
  async saveStages(rows: StageInput[], meta: Meta): Promise<Outcome> {
    const old = await this.stageUsage();
    const ids = rows.filter((r) => r.id !== null).map((r) => r.id as number);
    if (new Set(ids).size !== ids.length || ids.some((id) => !old.some((o) => o.id === id))) return { ok: false, error: 'The stage list is out of date. Reload the page and try again.' };
    const keep = new Set(rows.filter((r) => r.id !== null).map((r) => r.id));
    for (const st of old) {
      if (keep.has(st.id)) continue;
      if (st.used > 0) return { ok: false, error: `Move ${st.used} ${st.used === 1 ? 'opportunity' : 'opportunities'} out of "${st.name}" before deleting it.` };
    }
    for (const st of old) {
      if (keep.has(st.id)) continue;
      await this.q('DELETE crm.PipelineStages WHERE StageId = @id', [['id', sql.Int, st.id]]);
      await this.audit('stage', st.id, 'D', null, st.name, null, meta);
    }
    // Two phases so renames/swaps and moving the Won/Lost flag never collide with the unique indexes.
    await this.q(`UPDATE crm.PipelineStages SET StageType = 'open', Name = CONCAT(N'~', CAST(StageId AS nvarchar(12)))`);
    for (const [i, r] of rows.entries()) {
      const p: Param[] = [['n', sql.NVarChar(60), r.name], ['o', sql.SmallInt, i + 1], ['pr', sql.TinyInt, r.probability], ['t', sql.VarChar(4), r.type], ['by', sql.Int, meta.userId]];
      if (r.id === null) {
        const ins = this.rows(await this.q('INSERT crm.PipelineStages (Name, SortOrder, Probability, StageType, ModifiedBy) OUTPUT inserted.StageId AS Id VALUES (@n, @o, @pr, @t, @by)', p))[0];
        await this.audit('stage', NUM(ins?.Id), 'C', null, null, r.name, meta);
        continue;
      }
      await this.q('UPDATE crm.PipelineStages SET Name = @n, SortOrder = @o, Probability = @pr, StageType = @t, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE StageId = @id', [...p, ['id', sql.Int, r.id]]);
      const o = old.find((x) => x.id === r.id);
      if (!o) continue;
      for (const [f, a, b] of [['Name', o.name, r.name], ['Probability', o.probability, r.probability], ['StageType', o.type, r.type], ['SortOrder', o.sortOrder, i + 1]] as const) {
        if (a !== b) await this.audit('stage', r.id, 'U', f, String(a), String(b), meta);
      }
    }
    return { ok: true };
  }

  private async list(code: string) {
    const res = await this.q('SELECT v.ValueId, v.Code, v.Label, v.IsActive, v.SortOrder, p.Name AS PName FROM crm.Picklists p LEFT JOIN crm.PicklistValues v ON v.PicklistId = p.PicklistId WHERE p.Code = @c ORDER BY v.SortOrder, v.Label', [['c', sql.VarChar(30), code]]);
    const rows = this.rows(res);
    if (rows.length === 0) return null;
    const values: Array<PicklistValue & { sortOrder: number }> = rows.filter((r) => r.ValueId !== null).map((r) => ({ id: NUM(r.ValueId), code: (r.Code as string | null) ?? null, label: String(r.Label), active: Boolean(r.IsActive), sortOrder: NUM(r.SortOrder) }));
    return { name: String(rows[0]?.PName), values };
  }
  async picklist(code: string) { const l = await this.list(code); return l ? { name: l.name, values: l.values.map(({ sortOrder: _s, ...v }) => v) } : null; }
  private async duplicateLabel(code: string, label: string, exceptId: number | null) {
    const r = await this.q('SELECT 1 AS d FROM crm.PicklistValues v JOIN crm.Picklists p ON p.PicklistId = v.PicklistId WHERE p.Code = @c AND v.Label = @l AND (@id IS NULL OR v.ValueId <> @id)', [['c', sql.VarChar(30), code], ['l', sql.NVarChar(80), label], ['id', sql.Int, exceptId]]);
    return this.rows(r).length > 0;
  }
  async addPicklistValue(code: string, label: string, meta: Meta): Promise<Outcome> {
    if (code === 'activity_type') return { ok: false, error: 'Values cannot be added to this list.' };
    const l = await this.list(code);
    if (!l) return { ok: false, error: 'List not found.' };
    if (await this.duplicateLabel(code, label, null)) return { ok: false, error: `"${label}" already exists in this list.`, field: 'label' };
    const next = Math.max(0, ...l.values.map((v) => v.sortOrder)) + 1;
    const ins = this.rows(await this.q('INSERT crm.PicklistValues (PicklistId, Label, SortOrder, ModifiedBy) OUTPUT inserted.ValueId AS Id SELECT PicklistId, @l, @o, @by FROM crm.Picklists WHERE Code = @c',
      [['l', sql.NVarChar(80), label], ['o', sql.SmallInt, next], ['by', sql.Int, meta.userId], ['c', sql.VarChar(30), code]]))[0];
    await this.audit('picklistvalue', NUM(ins?.Id), 'C', null, null, label, meta);
    return { ok: true };
  }
  async renamePicklistValue(code: string, id: number, label: string, meta: Meta): Promise<Outcome> {
    const cur = (await this.list(code))?.values.find((v) => v.id === id);
    if (!cur) return { ok: false, error: 'Value not found.' };
    if (await this.duplicateLabel(code, label, id)) return { ok: false, error: `"${label}" already exists in this list.`, field: 'label' };
    if (cur.label !== label) {
      await this.q('UPDATE crm.PicklistValues SET Label = @l, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE ValueId = @id', [['l', sql.NVarChar(80), label], ['by', sql.Int, meta.userId], ['id', sql.Int, id]]);
      await this.audit('picklistvalue', id, 'U', 'Label', cur.label, label, meta);
    }
    return { ok: true };
  }
  async setPicklistActive(code: string, id: number, active: boolean, meta: Meta): Promise<Outcome> {
    const cur = (await this.list(code))?.values.find((v) => v.id === id);
    if (!cur) return { ok: false, error: 'Value not found.' };
    if (cur.active !== active) {
      await this.q('UPDATE crm.PicklistValues SET IsActive = @a, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE ValueId = @id', [['a', sql.Bit, active], ['by', sql.Int, meta.userId], ['id', sql.Int, id]]);
      await this.audit('picklistvalue', id, 'U', 'IsActive', cur.active ? '1' : '0', active ? '1' : '0', meta);
    }
    return { ok: true };
  }
  async movePicklistValue(code: string, id: number, dir: 'up' | 'down', meta: Meta): Promise<Outcome> {
    const vals = (await this.list(code))?.values;
    const i = vals?.findIndex((v) => v.id === id) ?? -1;
    if (!vals || i < 0) return { ok: false, error: 'Value not found.' };
    const j = dir === 'up' ? i - 1 : i + 1;
    const a = vals[i];
    const b = vals[j];
    if (!a || !b) return { ok: true };
    // Renumber the whole list so equal SortOrders never make the order ambiguous.
    const order = vals.map((v) => v.id);
    [order[i], order[j]] = [b.id, a.id];
    for (const [k, vid] of order.entries()) {
      await this.q('UPDATE crm.PicklistValues SET SortOrder = @o, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE ValueId = @id AND SortOrder <> @o', [['o', sql.SmallInt, k + 1], ['by', sql.Int, meta.userId], ['id', sql.Int, vid]]);
    }
    await this.audit('picklistvalue', id, 'U', 'SortOrder', String(i + 1), String(j + 1), meta);
    return { ok: true };
  }

  private static readonly TEAM = `SELECT t.TeamId, t.Name, LOWER(CONVERT(char(36), t.EntraGroupId)) AS GroupId, t.IsActive, (SELECT COUNT(*) FROM crm.Users u WHERE u.TeamId = t.TeamId) AS Members FROM crm.Teams t`;
  private toTeam = (r: Row): TeamRow => ({ id: NUM(r.TeamId), name: String(r.Name), groupId: String(r.GroupId), active: Boolean(r.IsActive), members: NUM(r.Members) });
  async teams() { return this.rows(await this.q(`${SqlAdminRepo.TEAM} ORDER BY t.Name`)).map(this.toTeam); }
  async team(id: number) { const r = this.rows(await this.q(`${SqlAdminRepo.TEAM} WHERE t.TeamId = @id`, [['id', sql.Int, id]]))[0]; return r ? this.toTeam(r) : null; }
  async saveTeam(i: { id: number | null; name: string; groupId: string }, meta: Meta) {
    const all = await this.teams();
    const nameDup = all.find((t) => t.id !== i.id && t.name.toLowerCase() === i.name.toLowerCase());
    if (nameDup) return { ok: false as const, field: 'name', error: `A team named ${nameDup.name} already exists.` };
    const groupDup = all.find((t) => t.id !== i.id && t.groupId === i.groupId);
    if (groupDup) return { ok: false as const, field: 'groupId', error: `This group is already linked to the team ${groupDup.name}.` };
    const p: Param[] = [['n', sql.NVarChar(80), i.name], ['g', sql.UniqueIdentifier, i.groupId], ['by', sql.Int, meta.userId]];
    if (i.id === null) {
      const ins = this.rows(await this.q('INSERT crm.Teams (Name, EntraGroupId, ModifiedBy) OUTPUT inserted.TeamId AS Id VALUES (@n, @g, @by)', p))[0];
      const id = NUM(ins?.Id);
      await this.audit('team', id, 'C', null, null, i.name, meta);
      return { ok: true as const, id };
    }
    const old = all.find((t) => t.id === i.id);
    if (!old) return { ok: false as const, error: 'Team not found.' };
    await this.q('UPDATE crm.Teams SET Name = @n, EntraGroupId = @g, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE TeamId = @id', [...p, ['id', sql.Int, i.id]]);
    if (old.name !== i.name) await this.audit('team', i.id, 'U', 'Name', old.name, i.name, meta);
    if (old.groupId !== i.groupId) await this.audit('team', i.id, 'U', 'EntraGroupId', old.groupId, i.groupId, meta);
    return { ok: true as const, id: i.id };
  }
  async setTeamActive(id: number, active: boolean, meta: Meta) {
    const old = await this.team(id);
    if (!old) return false;
    if (old.active !== active) {
      await this.q('UPDATE crm.Teams SET IsActive = @a, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE TeamId = @id', [['a', sql.Bit, active], ['by', sql.Int, meta.userId], ['id', sql.Int, id]]);
      await this.audit('team', id, 'U', 'IsActive', old.active ? '1' : '0', active ? '1' : '0', meta);
    }
    return true;
  }
  async users(): Promise<UserRow[]> {
    const res = await this.q(`SELECT u.UserId, u.DisplayName, u.Email, u.Role, u.TeamId, t.Name AS TeamName, u.TeamStatus, CONVERT(varchar(23), u.LastSignInAt, 126) + 'Z' AS LastSignIn, u.IsActive
      FROM crm.Users u LEFT JOIN crm.Teams t ON t.TeamId = u.TeamId ORDER BY u.DisplayName`);
    return this.rows(res).map((r) => ({ id: NUM(r.UserId), name: String(r.DisplayName), email: (r.Email as string | null) ?? null, role: String(r.Role), teamId: (r.TeamId as number | null) ?? null, teamName: (r.TeamName as string | null) ?? null, teamStatus: r.TeamStatus as UserRow['teamStatus'], lastSignIn: r.LastSignIn ? String(r.LastSignIn) : null, active: Boolean(r.IsActive) }));
  }
}
