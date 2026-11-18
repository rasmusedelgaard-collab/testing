"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.PAGE_SIZES = exports.MAX_INT = exports.ENTITY_LIST = exports.ENTITIES = void 0;
exports.parseListQuery = parseListQuery;
const format_1 = require("../services/format");
const text = (name, length) => ({ name, type: 'text', length });
const int = (name) => ({ name, type: 'int' });
exports.ENTITIES = {
    accounts: {
        key: 'accounts', table: 'crm.Accounts', pk: 'AccountId', path: '/accounts', singular: 'account', plural: 'Accounts', auditType: 'account', labelCol: 'Name',
        columns: [text('Name', 120), text('Website', 255), text('Phone', 40), int('IndustryValueId'), text('Street', 200), text('PostalCode', 20), text('City', 100), text('CountryCode', 2), text('Notes', 2000)],
        auditFields: ['OwnerId'],
        sorts: { name: { sql: 'e.Name', field: 'Name' }, industry: { sql: 'iv.Label', field: 'IndustryLabel' }, city: { sql: 'e.City', field: 'City' }, owner: { sql: 'uo.DisplayName', field: 'OwnerName' } },
        defaultSort: 'name', defaultDir: 'asc',
        search: [{ sql: 'e.Name', field: 'Name' }, { sql: 'e.City', field: 'City' }],
        filters: [{ key: 'owner', kind: 'int' }, { key: 'industry', kind: 'int' }],
    },
    contacts: {
        key: 'contacts', table: 'crm.Contacts', pk: 'ContactId', path: '/contacts', singular: 'contact', plural: 'Contacts', auditType: 'contact', labelCol: 'LastName',
        columns: [int('AccountId'), text('FirstName', 80), text('LastName', 80), text('JobTitle', 120), text('Email', 254), text('Phone', 40), text('Mobile', 40)],
        auditFields: ['OwnerId'],
        sorts: { name: { sql: 'e.LastName', field: 'LastName' }, account: { sql: 'a.Name', field: 'AccountName' }, title: { sql: 'e.JobTitle', field: 'JobTitle' }, email: { sql: 'e.Email', field: 'Email' }, owner: { sql: 'uo.DisplayName', field: 'OwnerName' } },
        defaultSort: 'name', defaultDir: 'asc',
        search: [{ sql: 'e.FirstName', field: 'FirstName' }, { sql: 'e.LastName', field: 'LastName' }, { sql: 'e.Email', field: 'Email' }, { sql: 'a.Name', field: 'AccountName' }],
        filters: [{ key: 'account', kind: 'int' }, { key: 'owner', kind: 'int' }],
    },
    opportunities: {
        key: 'opportunities', table: 'crm.Opportunities', pk: 'OpportunityId', path: '/opportunities', singular: 'opportunity', plural: 'Opportunities', auditType: 'opportunity', labelCol: 'Name',
        columns: [text('Name', 120), int('AccountId'), int('PrimaryContactId'), int('StageId'), { name: 'Amount', type: 'money' }, text('CurrencyCode', 3), { name: 'CloseDate', type: 'date' }, int('LeadSourceValueId'), int('LostReasonValueId')],
        auditFields: ['Amount', 'StageId', 'CurrencyCode', 'CloseDate', 'OwnerId'],
        sorts: { name: { sql: 'e.Name', field: 'Name' }, account: { sql: 'a.Name', field: 'AccountName' }, stage: { sql: 's.SortOrder', field: 'StageSort' }, amount: { sql: 'e.Amount', field: 'Amount' }, close: { sql: 'e.CloseDate', field: 'CloseDate' }, owner: { sql: 'uo.DisplayName', field: 'OwnerName' } },
        defaultSort: 'close', defaultDir: 'asc',
        search: [{ sql: 'e.Name', field: 'Name' }, { sql: 'a.Name', field: 'AccountName' }],
        filters: [{ key: 'stage', kind: 'int' }, { key: 'owner', kind: 'int' }, { key: 'from', kind: 'date' }, { key: 'to', kind: 'date' }, { key: 'status', kind: 'enum', values: ['open', 'won', 'lost', 'closed', 'all'], default: 'open' }, { key: 'account', kind: 'int' }, { key: 'contact', kind: 'int' }],
    },
    activities: {
        key: 'activities', table: 'crm.Activities', pk: 'ActivityId', path: '/activities', singular: 'activity', plural: 'Activities', auditType: 'activity', labelCol: 'Subject',
        columns: [text('ActivityType', 10), text('Subject', 160), text('Description', 4000), int('AccountId'), int('ContactId'), int('OpportunityId'), { name: 'DueDate', type: 'date' }, { name: 'DueTime', type: 'time' }, { name: 'IsDone', type: 'bool' }],
        auditFields: ['OwnerId'],
        sorts: { type: { sql: 'e.ActivityType', field: 'ActivityType' }, subject: { sql: 'e.Subject', field: 'Subject' }, due: { sql: 'e.DueDate', field: 'DueDate' }, owner: { sql: 'uo.DisplayName', field: 'OwnerName' } },
        defaultSort: 'due', defaultDir: 'asc',
        search: [{ sql: 'e.Subject', field: 'Subject' }],
        filters: [{ key: 'type', kind: 'enum', values: ['call', 'meeting', 'task', 'note', 'actionable'] }, { key: 'status', kind: 'enum', values: ['open', 'done', 'all'], default: 'open' }, { key: 'due', kind: 'enum', values: ['overdue', 'today', 'week', 'due', 'any'], default: 'any' }, { key: 'owner', kind: 'int' }, { key: 'account', kind: 'int' }, { key: 'contact', kind: 'int' }, { key: 'opportunity', kind: 'int' }],
    },
};
exports.ENTITY_LIST = Object.values(exports.ENTITIES);
/** SQL int upper bound: larger values would raise a conversion error (500). */
exports.MAX_INT = 2_147_483_647;
exports.PAGE_SIZES = [25, 50, 100];
/** Parses the URL query into a safe ListQuery: unknown sort keys, bad numbers and bad enum values fall back to defaults. */
function parseListQuery(def, query, today, maxPageSize = 100) {
    const str = (v) => (typeof v === 'string' ? v : '');
    const sort = Object.hasOwn(def.sorts, str(query.sort)) ? str(query.sort) : def.defaultSort;
    const dir = query.dir === 'desc' ? 'desc' : query.dir === 'asc' ? 'asc' : def.defaultDir;
    const pageSizeRaw = Number(str(query.pageSize));
    const pageSize = exports.PAGE_SIZES.includes(pageSizeRaw) && pageSizeRaw <= maxPageSize ? pageSizeRaw : 25;
    const pageRaw = Number(str(query.page));
    const page = Number.isInteger(pageRaw) && pageRaw >= 1 && pageRaw <= 100000 ? pageRaw : 1;
    const filters = {};
    for (const f of def.filters) {
        const v = str(query[f.key]).trim();
        if (f.kind === 'int' && /^[1-9]\d{0,9}$/.test(v) && Number(v) <= exports.MAX_INT)
            filters[f.key] = v;
        else if (f.kind === 'date' && (0, format_1.isRealDate)(v))
            filters[f.key] = v;
        else if (f.kind === 'enum') {
            if (f.values?.includes(v))
                filters[f.key] = v;
            else if (f.default)
                filters[f.key] = f.default;
        }
    }
    return { q: str(query.q).trim().slice(0, 100), page, pageSize, sort, dir, filters, today };
}
//# sourceMappingURL=entities.js.map