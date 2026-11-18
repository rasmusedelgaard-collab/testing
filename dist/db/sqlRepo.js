"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.sqlStore = exports.SqlRepo = void 0;
const mssql_1 = __importDefault(require("mssql"));
const entities_1 = require("../domain/entities");
const audit_1 = require("../services/audit");
const crmSql_1 = require("./crmSql");
const sqlAdmin_1 = require("./sqlAdmin");
const txQueue_1 = require("./txQueue");
const withRlsContext_1 = require("./withRlsContext");
const RV = /^0x[0-9A-Fa-f]{16}$/;
function sqlType(c) {
    switch (c.type) {
        case 'text': return mssql_1.default.NVarChar(c.length ?? 4000);
        case 'int': return mssql_1.default.Int;
        case 'date': return mssql_1.default.VarChar(10);
        case 'time': return mssql_1.default.VarChar(8);
        case 'money': return mssql_1.default.Decimal(19, 2);
        case 'bool': return mssql_1.default.Bit;
    }
}
const escapeLike = (s) => s.replace(/[\\%_[]/g, (m) => `\\${m}`);
/** All CRM SQL. Constructed per request around the transaction that withRlsContext opened. */
class SqlRepo {
    tx;
    ctx;
    constructor(tx, ctx) {
        this.tx = tx;
        this.ctx = ctx;
    }
    async q(text, params = []) {
        // Serialised per transaction: never two requests in flight on one connection.
        return (0, txQueue_1.serial)(this.tx, () => {
            const r = new mssql_1.default.Request(this.tx);
            for (const [n, t, v] of params)
                r.input(n, t, v);
            return r.query(text);
        });
    }
    rows(res, i = 0) {
        const sets = res.recordsets;
        return sets[i] ?? res.recordset ?? [];
    }
    async lookups() {
        const res = await this.q(`SELECT SettingKey, SettingValue FROM crm.Settings;
      SELECT StageId, Name, SortOrder, Probability, StageType FROM crm.PipelineStages ORDER BY SortOrder;
      SELECT p.Code AS PCode, v.ValueId, v.Code, v.Label, v.IsActive FROM crm.PicklistValues v JOIN crm.Picklists p ON p.PicklistId = v.PicklistId ORDER BY p.Code, v.SortOrder, v.Label;`);
        const kv = new Map(this.rows(res, 0).map((r) => [String(r.SettingKey), String(r.SettingValue)]));
        const settings = {
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
        const stages = this.rows(res, 1).map((r) => ({ id: Number(r.StageId), name: String(r.Name), sortOrder: Number(r.SortOrder), probability: Number(r.Probability), type: r.StageType }));
        const picklists = {};
        for (const r of this.rows(res, 2))
            (picklists[String(r.PCode)] ??= []).push({ id: Number(r.ValueId), code: r.Code ?? null, label: String(r.Label), active: Boolean(r.IsActive) });
        return { settings, stages, picklists };
    }
    mapOwner = (r) => ({ id: Number(r.UserId), name: String(r.DisplayName), teamName: r.TeamName ?? null, active: Boolean(r.IsActive) });
    static OWNER_SELECT = 'SELECT u.UserId, u.DisplayName, t.Name AS TeamName, u.IsActive FROM crm.Users u LEFT JOIN crm.Teams t ON t.TeamId = u.TeamId';
    /** Admin: every active user. Others: self plus ACTIVE members of their own team (the rule fn_RowWriteTarget enforces). */
    async owners() {
        const { userId, role, teamId } = this.ctx;
        const res = role === 'Admin'
            ? await this.q(`${SqlRepo.OWNER_SELECT} WHERE u.IsActive = 1 ORDER BY u.DisplayName`)
            : await this.q(`${SqlRepo.OWNER_SELECT} WHERE u.UserId = @me OR (u.IsActive = 1 AND @team IS NOT NULL AND u.TeamId = @team) ORDER BY u.DisplayName`, [['me', mssql_1.default.Int, userId], ['team', mssql_1.default.Int, teamId]]);
        return this.rows(res).map(this.mapOwner);
    }
    async ownerById(id) {
        const res = await this.q(`${SqlRepo.OWNER_SELECT} WHERE u.UserId = @id`, [['id', mssql_1.default.Int, id]]);
        const r = this.rows(res)[0];
        return r ? this.mapOwner(r) : null;
    }
    async refOptions(entity) {
        const d = entities_1.ENTITIES[entity];
        const label = entity === 'contacts' ? `LTRIM(CONCAT(FirstName, N' ', LastName))` : entity === 'activities' ? 'Subject' : 'Name';
        const res = await this.q(`SELECT TOP (500) ${d.pk} AS Id, ${label} AS Label FROM ${d.table} ORDER BY ${label}`);
        return this.rows(res).map((r) => ({ id: Number(r.Id), label: String(r.Label) }));
    }
    base(entity) {
        const d = entities_1.ENTITIES[entity];
        const s = crmSql_1.SQL[entity];
        return { d, from: `FROM ${d.table} e ${crmSql_1.COMMON_JOINS} ${s.joins}`, select: `SELECT e.${d.pk} AS Id, ${s.cols}, ${crmSql_1.COMMON_COLS}` };
    }
    async list(entity, q) {
        const { d, from, select } = this.base(entity);
        const params = [['today', mssql_1.default.VarChar(10), q.today]];
        const where = [];
        for (const f of d.filters) {
            const value = q.filters[f.key];
            if (value === undefined)
                continue;
            const clause = (0, crmSql_1.filterClause)(entity, f.key, value);
            if (!clause)
                continue;
            where.push(clause);
            if (clause.includes(`@f_${f.key}`))
                params.push([`f_${f.key}`, f.kind === 'int' ? mssql_1.default.Int : mssql_1.default.VarChar(10), f.kind === 'int' ? Number(value) : value]);
        }
        if (q.q) {
            where.push(`(${d.search.map((s) => `${s.sql} LIKE @q ESCAPE '\\'`).join(' OR ')})`);
            params.push(['q', mssql_1.default.NVarChar(120), `%${escapeLike(q.q)}%`]);
        }
        const sort = (Object.hasOwn(d.sorts, q.sort) ? d.sorts[q.sort] : undefined) ?? d.sorts[d.defaultSort];
        const dir = q.dir === 'desc' ? 'DESC' : 'ASC';
        const w = where.length ? `WHERE ${where.join(' AND ')}` : '';
        params.push(['off', mssql_1.default.Int, (q.page - 1) * q.pageSize], ['ps', mssql_1.default.Int, q.pageSize]);
        const res = await this.q(`SELECT COUNT(*) AS Total ${from} ${w};
      ${select} ${from} ${w} ORDER BY ${sort.sql} ${dir}, e.${d.pk} ${dir} OFFSET @off ROWS FETCH NEXT @ps ROWS ONLY;`, params);
        return { total: Number(this.rows(res, 0)[0]?.Total ?? 0), rows: this.rows(res, 1) };
    }
    async get(entity, id) {
        const { d, from, select } = this.base(entity);
        const res = await this.q(`${select} ${from} WHERE e.${d.pk} = @id`, [['id', mssql_1.default.Int, id]]);
        return this.rows(res)[0] ?? null;
    }
    async refExists(entity, id) {
        const d = entities_1.ENTITIES[entity];
        const res = await this.q(`SELECT 1 AS ok FROM ${d.table} WHERE ${d.pk} = @id`, [['id', mssql_1.default.Int, id]]);
        return this.rows(res).length > 0;
    }
    async history(entity, id) {
        const owner = (c) => `CASE WHEN a.FieldName = 'OwnerId' THEN (SELECT DisplayName FROM crm.Users WHERE UserId = TRY_CAST(a.${c} AS int)) ELSE a.${c} END`;
        const res = await this.q(`SELECT TOP (100) CONVERT(varchar(23), a.ChangedAt, 126) + 'Z' AS At, u.DisplayName AS ByName, a.Action, a.FieldName,
        ${owner('OldValue')} AS OldValue, ${owner('NewValue')} AS NewValue
      FROM crm.AuditLog a JOIN crm.Users u ON u.UserId = a.ChangedBy
      WHERE a.EntityType = @et AND a.EntityId = @id ORDER BY a.ChangedAt DESC, a.AuditId DESC`, [['et', mssql_1.default.VarChar(20), entities_1.ENTITIES[entity].auditType], ['id', mssql_1.default.Int, id]]);
        return this.rows(res).map((r) => ({ at: String(r.At), by: String(r.ByName), action: r.Action, field: r.FieldName ?? null, oldValue: r.OldValue ?? null, newValue: r.NewValue ?? null }));
    }
    async oppAggregates(f) {
        // Whitelisted fragments only: dimensions and types come from fixed sets, values are parameters.
        const DIM = {
            stage: { select: 'e.StageId AS StageId', group: 'e.StageId' },
            owner: { select: 'e.OwnerId AS OwnerId, uo.DisplayName AS OwnerName', group: 'e.OwnerId, uo.DisplayName', join: 'JOIN crm.Users uo ON uo.UserId = e.OwnerId' },
            leadSource: { select: 'ls.Label AS LeadSource', group: 'ls.Label', join: 'LEFT JOIN crm.PicklistValues ls ON ls.ValueId = e.LeadSourceValueId' },
            month: { select: "CONVERT(char(7), e.CloseDate, 23) AS Month", group: 'CONVERT(char(7), e.CloseDate, 23)' },
            account: { select: 'e.AccountId AS AccountId', group: 'e.AccountId' },
        };
        const dims = [...new Set(f.groupBy)].filter((d) => Object.hasOwn(DIM, d)).map((d) => DIM[d]);
        const types = [...new Set(f.types)].filter((t) => t === 'open' || t === 'won' || t === 'lost');
        if (types.length === 0)
            return [];
        const params = [];
        const where = [`s.StageType IN (${types.map((t) => `'${t}'`).join(', ')})`];
        if (f.from) {
            where.push('e.CloseDate >= @from');
            params.push(['from', mssql_1.default.VarChar(10), f.from]);
        }
        if (f.to) {
            where.push('e.CloseDate <= @to');
            params.push(['to', mssql_1.default.VarChar(10), f.to]);
        }
        if (f.ownerId) {
            where.push('e.OwnerId = @owner');
            params.push(['owner', mssql_1.default.Int, f.ownerId]);
        }
        if (f.teamId) {
            where.push('e.TeamId = @team');
            params.push(['team', mssql_1.default.Int, f.teamId]);
        }
        if (f.accountIds) {
            if (f.accountIds.length === 0)
                return [];
            where.push('e.AccountId IN (SELECT CAST([value] AS int) FROM OPENJSON(@acc))');
            params.push(['acc', mssql_1.default.NVarChar(mssql_1.default.MAX), JSON.stringify(f.accountIds.filter(Number.isInteger))]);
        }
        const res = await this.q(`SELECT s.StageType, e.CurrencyCode${dims.map((d) => `, ${d.select}`).join('')}, COUNT(*) AS Cnt, SUM(e.Amount) AS Amount
      FROM crm.Opportunities e JOIN crm.PipelineStages s ON s.StageId = e.StageId ${dims.map((d) => d.join ?? '').join(' ')}
      WHERE ${where.join(' AND ')} GROUP BY s.StageType, e.CurrencyCode${dims.map((d) => `, ${d.group}`).join('')}`, params);
        return this.rows(res).map((r) => ({
            stageType: r.StageType, stageId: r.StageId ?? null, ownerId: r.OwnerId ?? null, ownerName: r.OwnerName ?? null,
            leadSource: r.LeadSource ?? null, month: r.Month ?? null, accountId: r.AccountId ?? null,
            currency: String(r.CurrencyCode).trim(), count: Number(r.Cnt), amount: Number(r.Amount),
        }));
    }
    async fxRates(base) {
        const res = await this.q(`SELECT CurrencyCode, CONVERT(char(7), RateMonth, 23) AS Month, Rate FROM crm.FxRates WHERE BaseCurrency = @b`, [['b', mssql_1.default.Char(3), base]]);
        return this.rows(res).map((r) => ({ currency: String(r.CurrencyCode).trim(), month: String(r.Month), rate: Number(r.Rate) }));
    }
    async activityCounts(today) {
        const res = await this.q(`SELECT ISNULL(SUM(CASE WHEN DueDate < @t THEN 1 ELSE 0 END), 0) AS Overdue, ISNULL(SUM(CASE WHEN DueDate = @t THEN 1 ELSE 0 END), 0) AS Today,
        ISNULL(SUM(CASE WHEN DueDate > @t AND DueDate <= DATEADD(day, 7, CAST(@t AS date)) THEN 1 ELSE 0 END), 0) AS Next7
      FROM crm.Activities WHERE IsDone = 0 AND ActivityType <> 'note' AND DueDate IS NOT NULL`, [['t', mssql_1.default.VarChar(10), today]]);
        const r = this.rows(res)[0];
        return { overdue: Number(r?.Overdue ?? 0), today: Number(r?.Today ?? 0), next7: Number(r?.Next7 ?? 0) };
    }
    async teams() {
        const res = await this.q('SELECT TeamId, Name FROM crm.Teams WHERE IsActive = 1 ORDER BY Name');
        return this.rows(res).map((r) => ({ id: Number(r.TeamId), name: String(r.Name) }));
    }
    async activitiesByType(range, scope = {}) {
        const res = await this.q(`SELECT ActivityType, COUNT(*) AS Cnt FROM crm.Activities
      WHERE COALESCE(DueDate, CAST(CreatedAt AS date)) BETWEEN @from AND @to ${scope.ownerId ? 'AND OwnerId = @o' : ''} ${scope.teamId ? 'AND TeamId = @t' : ''} GROUP BY ActivityType`, [['from', mssql_1.default.VarChar(10), range.from], ['to', mssql_1.default.VarChar(10), range.to], ...(scope.ownerId ? [['o', mssql_1.default.Int, scope.ownerId]] : []), ...(scope.teamId ? [['t', mssql_1.default.Int, scope.teamId]] : [])]);
        return this.rows(res).map((r) => ({ type: String(r.ActivityType), count: Number(r.Cnt) }));
    }
    valueParams(entity, values) {
        const p = entities_1.ENTITIES[entity].columns.map((c) => [c.name, sqlType(c), values[c.name] ?? null]);
        p.push(['OwnerId', mssql_1.default.Int, values.OwnerId ?? null]);
        return p;
    }
    async writeAudit(rows, meta) {
        for (const a of rows) {
            await this.q(`INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId)
        VALUES (@et, @eid, @act, @fn, @ov, @nv, @owner, @team, @by, @cid)`, [
                ['et', mssql_1.default.VarChar(20), a.entityType], ['eid', mssql_1.default.Int, a.entityId], ['act', mssql_1.default.Char(1), a.action], ['fn', mssql_1.default.VarChar(64), a.fieldName],
                ['ov', mssql_1.default.NVarChar(4000), a.oldValue], ['nv', mssql_1.default.NVarChar(4000), a.newValue], ['owner', mssql_1.default.Int, a.ownerId], ['team', mssql_1.default.Int, a.teamId],
                ['by', mssql_1.default.Int, meta.userId], ['cid', mssql_1.default.UniqueIdentifier, meta.correlationId]
            ]);
        }
    }
    async create(entity, values, meta) {
        const d = entities_1.ENTITIES[entity];
        const x = crmSql_1.SQL[entity].insertExtra;
        const names = [...d.columns.map((c) => c.name), 'OwnerId', 'TeamId', 'CreatedBy', 'ModifiedBy', ...(x ? [x.col] : [])];
        const vals = [...d.columns.map((c) => `@${c.name}`), '@OwnerId', '@TeamId', '@By', '@By', ...(x ? [x.expr] : [])];
        const res = await this.q(`DECLARE @TeamId int = (SELECT TeamId FROM crm.Users WHERE UserId = @OwnerId);
      INSERT ${d.table} (${names.join(', ')}) OUTPUT inserted.${d.pk} AS Id, inserted.OwnerId, inserted.TeamId VALUES (${vals.join(', ')});`, [...this.valueParams(entity, values), ['By', mssql_1.default.Int, meta.userId]]);
        const r = this.rows(res)[0];
        if (!r)
            throw new Error('insert returned no row');
        const id = Number(r.Id);
        await this.writeAudit([(0, audit_1.createRow)(entity, id, String(values[d.labelCol] ?? ''), Number(r.OwnerId), r.TeamId ?? null)], meta);
        return id;
    }
    async update(entity, id, values, rowVer, meta) {
        const d = entities_1.ENTITIES[entity];
        if (!RV.test(rowVer))
            return 'conflict';
        const x = crmSql_1.SQL[entity].updateExtra;
        const sets = [...d.columns.map((c) => `${c.name} = @${c.name}`), 'OwnerId = @OwnerId', 'TeamId = (SELECT TeamId FROM crm.Users WHERE UserId = @OwnerId)', 'ModifiedBy = @By', 'ModifiedAt = SYSUTCDATETIME()', ...(x ? [`${x.col} = ${x.expr}`] : [])];
        const out = [...new Set(d.auditFields)].flatMap((f) => [`deleted.${f} AS o_${f}`, `inserted.${f} AS n_${f}`]);
        const res = await this.q(`UPDATE ${d.table} SET ${sets.join(', ')} OUTPUT ${out.join(', ')}, inserted.TeamId AS n_TeamId
      WHERE ${d.pk} = @id AND RowVer = CONVERT(binary(8), @rv, 1)`, [...this.valueParams(entity, values), ['By', mssql_1.default.Int, meta.userId], ['id', mssql_1.default.Int, id], ['rv', mssql_1.default.VarChar(20), rowVer]]);
        const r = this.rows(res)[0];
        if (!r) {
            // Not updated: either someone else changed it (409) or it is gone / hidden by RLS (404).
            const exists = await this.q(`SELECT 1 AS ok FROM ${d.table} WHERE ${d.pk} = @id`, [['id', mssql_1.default.Int, id]]);
            return this.rows(exists).length > 0 ? 'conflict' : 'notfound';
        }
        const pick = (p) => Object.fromEntries(d.auditFields.map((f) => [f, r[`${p}_${f}`] ?? null]));
        let stageNames;
        if (d.auditFields.includes('StageId')) {
            const st = this.rows(await this.q('SELECT StageId, Name FROM crm.PipelineStages'));
            stageNames = new Map(st.map((s) => [Number(s.StageId), String(s.Name)]));
        }
        const rows = (0, audit_1.updateRows)(entity, id, pick('o'), pick('n'), Number(r.n_OwnerId), r.n_TeamId ?? null, (f, v) => (f === 'StageId' && typeof v === 'number' ? (stageNames?.get(v) ?? v) : v));
        await this.writeAudit(rows, meta);
        return 'ok';
    }
    async remove(entity, id, meta) {
        const d = entities_1.ENTITIES[entity];
        // No cascades: unlink the rows we can see first. Rows we cannot see, or opportunities, make the DELETE fail with 547.
        for (const stmt of crmSql_1.SQL[entity].clearLinks)
            await this.q(stmt, [['id', mssql_1.default.Int, id]]);
        const res = await this.q(`DELETE ${d.table} OUTPUT deleted.OwnerId, deleted.TeamId, deleted.${d.labelCol} AS Label WHERE ${d.pk} = @id`, [['id', mssql_1.default.Int, id]]);
        const r = this.rows(res)[0];
        if (!r)
            return false;
        await this.writeAudit([(0, audit_1.deleteRow)(entity, id, String(r.Label ?? ''), Number(r.OwnerId), r.TeamId ?? null)], meta);
        return true;
    }
    async setDone(id, done, meta) {
        const res = await this.q(`UPDATE crm.Activities SET IsDone = @d, DoneAt = CASE WHEN @d = 1 THEN SYSUTCDATETIME() END, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME()
      OUTPUT inserted.ActivityId AS Id WHERE ActivityId = @id`, [['d', mssql_1.default.Bit, done], ['by', mssql_1.default.Int, meta.userId], ['id', mssql_1.default.Int, id]]);
        return this.rows(res).length > 0;
    }
}
exports.SqlRepo = SqlRepo;
exports.sqlStore = {
    run: (ctx, fn) => (0, withRlsContext_1.withRlsContext)(ctx, (tx) => fn(new SqlRepo(tx, ctx))),
    runAdmin: (ctx, fn) => (0, withRlsContext_1.withRlsContext)(ctx, (tx) => fn(new sqlAdmin_1.SqlAdminRepo(tx, ctx))),
};
//# sourceMappingURL=sqlRepo.js.map