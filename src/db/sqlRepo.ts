import sql from 'mssql';
import { ENTITIES, type ColumnSpec } from '../domain/entities';
import type { AggRow, CrmStore, EntityKey, FxRateRow, HistoryRow, OppAggFilter, ListQuery, Lookups, Meta, OwnerOption, PicklistValue, RefOption, Repo, Row, Settings, Stage, UpdateResult, Values } from '../domain/types';
import { createRow, deleteRow, updateRows, type AuditRow } from '../services/audit';
import { COMMON_COLS, COMMON_JOINS, filterClause, SQL } from './crmSql';
import { SqlAdminRepo } from './sqlAdmin';
import { withRlsContext, type RlsContext } from './withRlsContext';

type Param = [name: string, type: unknown, value: unknown];
const RV = /^0x[0-9A-Fa-f]{16}$/;

function sqlType(c: ColumnSpec) {
  switch (c.type) {
    case 'text': return sql.NVarChar(c.length ?? 4000);
    case 'int': return sql.Int;
    case 'date': return sql.VarChar(10);
    case 'time': return sql.VarChar(8);
    case 'money': return sql.Decimal(19, 2);
    case 'bool': return sql.Bit;
  }
}
const escapeLike = (s: string) => s.replace(/[\\%_[]/g, (m) => `\\${m}`);

/** All CRM SQL. Constructed per request around the transaction that withRlsContext opened. */
export class SqlRepo implements Repo {
  constructor(private readonly tx: sql.Transaction, private readonly ctx: RlsContext) {}

  private async q(text: string, params: Param[] = []) {
    const r = new sql.Request(this.tx);
    for (const [n, t, v] of params) r.input(n, t as sql.ISqlType, v);
    return r.query(text);
  }
  private rows(res: Awaited<ReturnType<SqlRepo['q']>>, i = 0): Row[] {
    const sets = res.recordsets as unknown as Row[][];
    return sets[i] ?? (res.recordset as unknown as Row[]) ?? [];
  }

  async lookups(): Promise<Lookups> {
    const res = await this.q(`SELECT SettingKey, SettingValue FROM crm.Settings;
      SELECT StageId, Name, SortOrder, Probability, StageType FROM crm.PipelineStages ORDER BY SortOrder;
      SELECT p.Code AS PCode, v.ValueId, v.Code, v.Label, v.IsActive FROM crm.PicklistValues v JOIN crm.Picklists p ON p.PicklistId = v.PicklistId ORDER BY p.Code, v.SortOrder, v.Label;`);
    const kv = new Map(this.rows(res, 0).map((r) => [String(r.SettingKey), String(r.SettingValue)]));
    const settings: Settings = {
      appName: kv.get('app.name') ?? 'CRM',
      localeCode: kv.get('locale.code') ?? 'en-GB',
      timezone: kv.get('locale.timezone') ?? 'UTC',
      baseCurrency: kv.get('currency.base') ?? 'DKK',
      enabledCurrencies: (kv.get('currency.enabled') ?? 'DKK').split(',').map((c) => c.trim()).filter(Boolean),
      visibilityMode: kv.get('visibility.mode') === 'team' ? 'team' : 'private',
      accentColor: kv.get('branding.accentColor') ?? '#1D4ED8',
      firstDayOfWeek: Number(kv.get('locale.firstDayOfWeek') ?? 1) || 1,
      fiscalStartMonth: Number(kv.get('fiscal.startMonth') ?? 1) || 1,
    };
    const stages: Stage[] = this.rows(res, 1).map((r) => ({ id: Number(r.StageId), name: String(r.Name), sortOrder: Number(r.SortOrder), probability: Number(r.Probability), type: r.StageType as Stage['type'] }));
    const picklists: Record<string, PicklistValue[]> = {};
    for (const r of this.rows(res, 2)) (picklists[String(r.PCode)] ??= []).push({ id: Number(r.ValueId), code: (r.Code as string | null) ?? null, label: String(r.Label), active: Boolean(r.IsActive) });
    return { settings, stages, picklists };
  }

  private mapOwner = (r: Row): OwnerOption => ({ id: Number(r.UserId), name: String(r.DisplayName), teamName: (r.TeamName as string | null) ?? null, active: Boolean(r.IsActive) });
  private static readonly OWNER_SELECT = 'SELECT u.UserId, u.DisplayName, t.Name AS TeamName, u.IsActive FROM crm.Users u LEFT JOIN crm.Teams t ON t.TeamId = u.TeamId';

  /** Admin: every active user. Others: self plus ACTIVE members of their own team (the rule fn_RowWriteTarget enforces). */
  async owners(): Promise<OwnerOption[]> {
    const { userId, role, teamId } = this.ctx;
    const res = role === 'Admin'
      ? await this.q(`${SqlRepo.OWNER_SELECT} WHERE u.IsActive = 1 ORDER BY u.DisplayName`)
      : await this.q(`${SqlRepo.OWNER_SELECT} WHERE u.UserId = @me OR (u.IsActive = 1 AND @team IS NOT NULL AND u.TeamId = @team) ORDER BY u.DisplayName`, [['me', sql.Int, userId], ['team', sql.Int, teamId]]);
    return this.rows(res).map(this.mapOwner);
  }
  async ownerById(id: number): Promise<OwnerOption | null> {
    const res = await this.q(`${SqlRepo.OWNER_SELECT} WHERE u.UserId = @id`, [['id', sql.Int, id]]);
    const r = this.rows(res)[0];
    return r ? this.mapOwner(r) : null;
  }

  async refOptions(entity: EntityKey): Promise<RefOption[]> {
    const d = ENTITIES[entity];
    const label = entity === 'contacts' ? `LTRIM(CONCAT(FirstName, N' ', LastName))` : entity === 'activities' ? 'Subject' : 'Name';
    const res = await this.q(`SELECT TOP (500) ${d.pk} AS Id, ${label} AS Label FROM ${d.table} ORDER BY ${label}`);
    return this.rows(res).map((r) => ({ id: Number(r.Id), label: String(r.Label) }));
  }

  private base(entity: EntityKey) {
    const d = ENTITIES[entity];
    const s = SQL[entity];
    return { d, from: `FROM ${d.table} e ${COMMON_JOINS} ${s.joins}`, select: `SELECT e.${d.pk} AS Id, ${s.cols}, ${COMMON_COLS}` };
  }

  async list(entity: EntityKey, q: ListQuery): Promise<{ rows: Row[]; total: number }> {
    const { d, from, select } = this.base(entity);
    const params: Param[] = [['today', sql.VarChar(10), q.today]];
    const where: string[] = [];
    for (const f of d.filters) {
      const value = q.filters[f.key];
      if (value === undefined) continue;
      const clause = filterClause(entity, f.key, value);
      if (!clause) continue;
      where.push(clause);
      if (clause.includes(`@f_${f.key}`)) params.push([`f_${f.key}`, f.kind === 'int' ? sql.Int : sql.VarChar(10), f.kind === 'int' ? Number(value) : value]);
    }
    if (q.q) {
      where.push(`(${d.search.map((s) => `${s.sql} LIKE @q ESCAPE '\\'`).join(' OR ')})`);
      params.push(['q', sql.NVarChar(120), `%${escapeLike(q.q)}%`]);
    }
    const sort = (Object.hasOwn(d.sorts, q.sort) ? d.sorts[q.sort] : undefined) ?? d.sorts[d.defaultSort]!;
    const dir = q.dir === 'desc' ? 'DESC' : 'ASC';
    const w = where.length ? `WHERE ${where.join(' AND ')}` : '';
    params.push(['off', sql.Int, (q.page - 1) * q.pageSize], ['ps', sql.Int, q.pageSize]);
    const res = await this.q(`SELECT COUNT(*) AS Total ${from} ${w};
      ${select} ${from} ${w} ORDER BY ${sort.sql} ${dir}, e.${d.pk} ${dir} OFFSET @off ROWS FETCH NEXT @ps ROWS ONLY;`, params);
    return { total: Number(this.rows(res, 0)[0]?.Total ?? 0), rows: this.rows(res, 1) };
  }

  async get(entity: EntityKey, id: number): Promise<Row | null> {
    const { d, from, select } = this.base(entity);
    const res = await this.q(`${select} ${from} WHERE e.${d.pk} = @id`, [['id', sql.Int, id]]);
    return this.rows(res)[0] ?? null;
  }

  async refExists(entity: EntityKey, id: number): Promise<boolean> {
    const d = ENTITIES[entity];
    const res = await this.q(`SELECT 1 AS ok FROM ${d.table} WHERE ${d.pk} = @id`, [['id', sql.Int, id]]);
    return this.rows(res).length > 0;
  }

  async history(entity: EntityKey, id: number): Promise<HistoryRow[]> {
    const owner = (c: string) => `CASE WHEN a.FieldName = 'OwnerId' THEN (SELECT DisplayName FROM crm.Users WHERE UserId = TRY_CAST(a.${c} AS int)) ELSE a.${c} END`;
    const res = await this.q(`SELECT TOP (100) CONVERT(varchar(23), a.ChangedAt, 126) + 'Z' AS At, u.DisplayName AS ByName, a.Action, a.FieldName,
        ${owner('OldValue')} AS OldValue, ${owner('NewValue')} AS NewValue
      FROM crm.AuditLog a JOIN crm.Users u ON u.UserId = a.ChangedBy
      WHERE a.EntityType = @et AND a.EntityId = @id ORDER BY a.ChangedAt DESC, a.AuditId DESC`, [['et', sql.VarChar(20), ENTITIES[entity].auditType], ['id', sql.Int, id]]);
    return this.rows(res).map((r) => ({ at: String(r.At), by: String(r.ByName), action: r.Action as HistoryRow['action'], field: (r.FieldName as string | null) ?? null, oldValue: (r.OldValue as string | null) ?? null, newValue: (r.NewValue as string | null) ?? null }));
  }

  async oppAggregates(f: OppAggFilter): Promise<AggRow[]> {
    // Whitelisted fragments only: dimensions and types come from fixed sets, values are parameters.
    const DIM: Record<string, { select: string; group: string; join?: string }> = {
      stage: { select: 'e.StageId AS StageId', group: 'e.StageId' },
      owner: { select: 'e.OwnerId AS OwnerId, uo.DisplayName AS OwnerName', group: 'e.OwnerId, uo.DisplayName', join: 'JOIN crm.Users uo ON uo.UserId = e.OwnerId' },
      leadSource: { select: 'ls.Label AS LeadSource', group: 'ls.Label', join: 'LEFT JOIN crm.PicklistValues ls ON ls.ValueId = e.LeadSourceValueId' },
      month: { select: "CONVERT(char(7), e.CloseDate, 23) AS Month", group: 'CONVERT(char(7), e.CloseDate, 23)' },
      account: { select: 'e.AccountId AS AccountId', group: 'e.AccountId' },
    };
    const dims = [...new Set(f.groupBy)].filter((d) => Object.hasOwn(DIM, d)).map((d) => DIM[d]!);
    const types = [...new Set(f.types)].filter((t) => t === 'open' || t === 'won' || t === 'lost');
    if (types.length === 0) return [];
    const params: Param[] = [];
    const where = [`s.StageType IN (${types.map((t) => `'${t}'`).join(', ')})`];
    if (f.from) { where.push('e.CloseDate >= @from'); params.push(['from', sql.VarChar(10), f.from]); }
    if (f.to) { where.push('e.CloseDate <= @to'); params.push(['to', sql.VarChar(10), f.to]); }
    if (f.ownerId) { where.push('e.OwnerId = @owner'); params.push(['owner', sql.Int, f.ownerId]); }
    if (f.accountIds) {
      if (f.accountIds.length === 0) return [];
      where.push('e.AccountId IN (SELECT CAST([value] AS int) FROM OPENJSON(@acc))');
      params.push(['acc', sql.NVarChar(sql.MAX), JSON.stringify(f.accountIds.filter(Number.isInteger))]);
    }
    const res = await this.q(`SELECT s.StageType, e.CurrencyCode${dims.map((d) => `, ${d.select}`).join('')}, COUNT(*) AS Cnt, SUM(e.Amount) AS Amount
      FROM crm.Opportunities e JOIN crm.PipelineStages s ON s.StageId = e.StageId ${dims.map((d) => d.join ?? '').join(' ')}
      WHERE ${where.join(' AND ')} GROUP BY s.StageType, e.CurrencyCode${dims.map((d) => `, ${d.group}`).join('')}`, params);
    return this.rows(res).map((r) => ({
      stageType: r.StageType as AggRow['stageType'], stageId: (r.StageId as number | undefined) ?? null, ownerId: (r.OwnerId as number | undefined) ?? null, ownerName: (r.OwnerName as string | undefined) ?? null,
      leadSource: (r.LeadSource as string | null | undefined) ?? null, month: (r.Month as string | undefined) ?? null, accountId: (r.AccountId as number | undefined) ?? null,
      currency: String(r.CurrencyCode).trim(), count: Number(r.Cnt), amount: Number(r.Amount),
    }));
  }

  async fxRates(base: string): Promise<FxRateRow[]> {
    const res = await this.q(`SELECT CurrencyCode, CONVERT(char(7), RateMonth, 23) AS Month, Rate FROM crm.FxRates WHERE BaseCurrency = @b`, [['b', sql.Char(3), base]]);
    return this.rows(res).map((r) => ({ currency: String(r.CurrencyCode).trim(), month: String(r.Month), rate: Number(r.Rate) }));
  }

  async activityCounts(today: string): Promise<{ overdue: number; today: number; next7: number }> {
    const res = await this.q(`SELECT ISNULL(SUM(CASE WHEN DueDate < @t THEN 1 ELSE 0 END), 0) AS Overdue, ISNULL(SUM(CASE WHEN DueDate = @t THEN 1 ELSE 0 END), 0) AS Today,
        ISNULL(SUM(CASE WHEN DueDate > @t AND DueDate <= DATEADD(day, 7, CAST(@t AS date)) THEN 1 ELSE 0 END), 0) AS Next7
      FROM crm.Activities WHERE IsDone = 0 AND ActivityType <> 'note' AND DueDate IS NOT NULL`, [['t', sql.VarChar(10), today]]);
    const r = this.rows(res)[0];
    return { overdue: Number(r?.Overdue ?? 0), today: Number(r?.Today ?? 0), next7: Number(r?.Next7 ?? 0) };
  }

  async activitiesByType(range: { from: string; to: string }, ownerId?: number): Promise<Array<{ type: string; count: number }>> {
    const res = await this.q(`SELECT ActivityType, COUNT(*) AS Cnt FROM crm.Activities
      WHERE COALESCE(DueDate, CAST(CreatedAt AS date)) BETWEEN @from AND @to ${ownerId ? 'AND OwnerId = @o' : ''} GROUP BY ActivityType`,
    [['from', sql.VarChar(10), range.from], ['to', sql.VarChar(10), range.to], ...(ownerId ? ([['o', sql.Int, ownerId]] as Param[]) : [])]);
    return this.rows(res).map((r) => ({ type: String(r.ActivityType), count: Number(r.Cnt) }));
  }

  private valueParams(entity: EntityKey, values: Values): Param[] {
    const p: Param[] = ENTITIES[entity].columns.map((c) => [c.name, sqlType(c), values[c.name] ?? null]);
    p.push(['OwnerId', sql.Int, values.OwnerId ?? null]);
    return p;
  }

  private async writeAudit(rows: AuditRow[], meta: Meta): Promise<void> {
    for (const a of rows) {
      await this.q(`INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId)
        VALUES (@et, @eid, @act, @fn, @ov, @nv, @owner, @team, @by, @cid)`, [
        ['et', sql.VarChar(20), a.entityType], ['eid', sql.Int, a.entityId], ['act', sql.Char(1), a.action], ['fn', sql.VarChar(64), a.fieldName],
        ['ov', sql.NVarChar(4000), a.oldValue], ['nv', sql.NVarChar(4000), a.newValue], ['owner', sql.Int, a.ownerId], ['team', sql.Int, a.teamId],
        ['by', sql.Int, meta.userId], ['cid', sql.UniqueIdentifier, meta.correlationId]]);
    }
  }

  async create(entity: EntityKey, values: Values, meta: Meta): Promise<number> {
    const d = ENTITIES[entity];
    const x = SQL[entity].insertExtra;
    const names = [...d.columns.map((c) => c.name), 'OwnerId', 'TeamId', 'CreatedBy', 'ModifiedBy', ...(x ? [x.col] : [])];
    const vals = [...d.columns.map((c) => `@${c.name}`), '@OwnerId', '@TeamId', '@By', '@By', ...(x ? [x.expr] : [])];
    const res = await this.q(`DECLARE @TeamId int = (SELECT TeamId FROM crm.Users WHERE UserId = @OwnerId);
      INSERT ${d.table} (${names.join(', ')}) OUTPUT inserted.${d.pk} AS Id, inserted.OwnerId, inserted.TeamId VALUES (${vals.join(', ')});`,
    [...this.valueParams(entity, values), ['By', sql.Int, meta.userId]]);
    const r = this.rows(res)[0];
    if (!r) throw new Error('insert returned no row');
    const id = Number(r.Id);
    await this.writeAudit([createRow(entity, id, String(values[d.labelCol] ?? ''), Number(r.OwnerId), (r.TeamId as number | null) ?? null)], meta);
    return id;
  }

  async update(entity: EntityKey, id: number, values: Values, rowVer: string, meta: Meta): Promise<UpdateResult> {
    const d = ENTITIES[entity];
    if (!RV.test(rowVer)) return 'conflict';
    const x = SQL[entity].updateExtra;
    const sets = [...d.columns.map((c) => `${c.name} = @${c.name}`), 'OwnerId = @OwnerId', 'TeamId = (SELECT TeamId FROM crm.Users WHERE UserId = @OwnerId)', 'ModifiedBy = @By', 'ModifiedAt = SYSUTCDATETIME()', ...(x ? [`${x.col} = ${x.expr}`] : [])];
    const out = [...new Set(d.auditFields)].flatMap((f) => [`deleted.${f} AS o_${f}`, `inserted.${f} AS n_${f}`]);
    const res = await this.q(`UPDATE ${d.table} SET ${sets.join(', ')} OUTPUT ${out.join(', ')}, inserted.TeamId AS n_TeamId
      WHERE ${d.pk} = @id AND RowVer = CONVERT(binary(8), @rv, 1)`,
    [...this.valueParams(entity, values), ['By', sql.Int, meta.userId], ['id', sql.Int, id], ['rv', sql.VarChar(20), rowVer]]);
    const r = this.rows(res)[0];
    if (!r) {
      // Not updated: either someone else changed it (409) or it is gone / hidden by RLS (404).
      const exists = await this.q(`SELECT 1 AS ok FROM ${d.table} WHERE ${d.pk} = @id`, [['id', sql.Int, id]]);
      return this.rows(exists).length > 0 ? 'conflict' : 'notfound';
    }
    const pick = (p: string): Row => Object.fromEntries(d.auditFields.map((f) => [f, (r[`${p}_${f}`] as Row[string]) ?? null]));
    let stageNames: Map<number, string> | undefined;
    if (d.auditFields.includes('StageId')) {
      const st = this.rows(await this.q('SELECT StageId, Name FROM crm.PipelineStages'));
      stageNames = new Map(st.map((s) => [Number(s.StageId), String(s.Name)]));
    }
    const rows = updateRows(entity, id, pick('o'), pick('n'), Number(r.n_OwnerId), (r.n_TeamId as number | null) ?? null,
      (f, v) => (f === 'StageId' && typeof v === 'number' ? (stageNames?.get(v) ?? v) : v));
    await this.writeAudit(rows, meta);
    return 'ok';
  }

  async remove(entity: EntityKey, id: number, meta: Meta): Promise<boolean> {
    const d = ENTITIES[entity];
    // No cascades: unlink the rows we can see first. Rows we cannot see, or opportunities, make the DELETE fail with 547.
    for (const stmt of SQL[entity].clearLinks) await this.q(stmt, [['id', sql.Int, id]]);
    const res = await this.q(`DELETE ${d.table} OUTPUT deleted.OwnerId, deleted.TeamId, deleted.${d.labelCol} AS Label WHERE ${d.pk} = @id`, [['id', sql.Int, id]]);
    const r = this.rows(res)[0];
    if (!r) return false;
    await this.writeAudit([deleteRow(entity, id, String(r.Label ?? ''), Number(r.OwnerId), (r.TeamId as number | null) ?? null)], meta);
    return true;
  }

  async setDone(id: number, done: boolean, meta: Meta): Promise<boolean> {
    const res = await this.q(`UPDATE crm.Activities SET IsDone = @d, DoneAt = CASE WHEN @d = 1 THEN SYSUTCDATETIME() END, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME()
      OUTPUT inserted.ActivityId AS Id WHERE ActivityId = @id`, [['d', sql.Bit, done], ['by', sql.Int, meta.userId], ['id', sql.Int, id]]);
    return this.rows(res).length > 0;
  }
}

export const sqlStore: CrmStore = {
  run: (ctx, fn) => withRlsContext(ctx, (tx) => fn(new SqlRepo(tx, ctx))),
  runAdmin: (ctx, fn) => withRlsContext(ctx, (tx) => fn(new SqlAdminRepo(tx, ctx))),
};
