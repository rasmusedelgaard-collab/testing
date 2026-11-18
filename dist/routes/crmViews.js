"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.TYPE_LABEL = void 0;
exports.ownerLabel = ownerLabel;
exports.recordName = recordName;
exports.relatedTo = relatedTo;
exports.columnsFor = columnsFor;
exports.cellsFor = cellsFor;
exports.detailRows = detailRows;
exports.describeHistory = describeHistory;
exports.canDelete = canDelete;
exports.filterFields = filterFields;
exports.formValuesFromRow = formValuesFromRow;
exports.formFields = formFields;
exports.errorList = errorList;
exports.pageHref = pageHref;
const entities_1 = require("../domain/entities");
const s = (v) => (v === null || v === undefined ? '' : String(v));
const num = (v) => (typeof v === 'number' ? v : Number(v));
exports.TYPE_LABEL = { call: 'Call', meeting: 'Meeting', task: 'Task', note: 'Note' };
const cap = (x) => x.charAt(0).toUpperCase() + x.slice(1);
function ownerLabel(o, admin) {
    return admin && o.teamName ? `${o.name} (${o.teamName})` : o.name;
}
function recordName(entity, r) {
    if (entity === 'contacts')
        return [s(r.FirstName), s(r.LastName)].filter(Boolean).join(' ');
    return s(r[entities_1.ENTITIES[entity].labelCol === 'Subject' ? 'Subject' : entities_1.ENTITIES[entity].labelCol === 'LastName' ? 'LastName' : 'Name']);
}
function relatedTo(r) {
    if (r.OpportunityId)
        return { text: s(r.OpportunityName) || 'Opportunity', href: `/opportunities/${r.OpportunityId}` };
    if (r.ContactId)
        return { text: s(r.ContactName) || 'Contact', href: `/contacts/${r.ContactId}` };
    if (r.AccountId)
        return { text: s(r.AccountName) || 'Account', href: `/accounts/${r.AccountId}` };
    return { text: '(deleted record)' };
}
function columnsFor(entity) {
    switch (entity) {
        case 'accounts': return [{ label: 'Name', sort: 'name' }, { label: 'Industry', sort: 'industry' }, { label: 'City', sort: 'city' }, { label: 'Owner', sort: 'owner' }, { label: 'Open pipeline' }];
        case 'contacts': return [{ label: 'Name', sort: 'name' }, { label: 'Account', sort: 'account' }, { label: 'Title', sort: 'title' }, { label: 'Email', sort: 'email' }, { label: 'Phone' }, { label: 'Owner', sort: 'owner' }];
        case 'opportunities': return [{ label: 'Name', sort: 'name' }, { label: 'Account', sort: 'account' }, { label: 'Stage', sort: 'stage' }, { label: 'Amount', sort: 'amount' }, { label: 'Close date', sort: 'close' }, { label: 'Owner', sort: 'owner' }];
        case 'activities': return [{ label: 'Done' }, { label: 'Type', sort: 'type' }, { label: 'Subject', sort: 'subject' }, { label: 'Related to' }, { label: 'Due date', sort: 'due' }, { label: 'Owner', sort: 'owner' }];
    }
}
function cellsFor(entity, r, f, today, returnTo) {
    const path = entities_1.ENTITIES[entity].path;
    const link = `${path}/${r.Id}`;
    switch (entity) {
        case 'accounts': return [{ text: s(r.Name), href: link }, { text: s(r.IndustryLabel) }, { text: s(r.City) }, { text: s(r.OwnerName) }, { text: s(r.OpenPipeline) || '–', right: true }];
        case 'contacts': return [{ text: recordName(entity, r), href: link }, { text: s(r.AccountName), href: r.AccountId ? `/accounts/${r.AccountId}` : undefined }, { text: s(r.JobTitle) }, { text: s(r.Email) }, { text: s(r.Phone) }, { text: s(r.OwnerName) }];
        case 'opportunities': {
            const past = r.StageType === 'open' && s(r.CloseDate) < today;
            return [{ text: s(r.Name), href: link }, { text: s(r.AccountName), href: `/accounts/${r.AccountId}` }, { text: s(r.StageName) },
                { text: f.money(num(r.Amount), s(r.CurrencyCode)), right: true }, { text: f.date(s(r.CloseDate)), note: past ? 'Past close date' : undefined }, { text: s(r.OwnerName) }];
        }
        case 'activities': {
            const done = Boolean(r.IsDone);
            return [{ text: done ? 'Done' : 'Open', doneForm: { action: `${path}/${r.Id}/${done ? 'undone' : 'done'}`, context: done ? `${s(r.Subject)}, mark as not done` : s(r.Subject), checked: done, returnTo } },
                { text: exports.TYPE_LABEL[s(r.ActivityType)] ?? s(r.ActivityType) }, { text: s(r.Subject), href: link }, relatedTo(r),
                { text: f.date(s(r.DueDate)) + (r.DueTime ? ` ${s(r.DueTime)}` : '') }, { text: s(r.OwnerName) }];
        }
    }
}
function detailRows(entity, r, f) {
    const d = (label, text, href) => ({ label, text, href });
    switch (entity) {
        case 'accounts': return [d('Industry', s(r.IndustryLabel)), d('Website', s(r.Website)), d('Phone', s(r.Phone)), d('Street', s(r.Street)), d('Postcode', s(r.PostalCode)), d('City', s(r.City)), d('Country', s(r.CountryCode)), d('Notes', s(r.Notes))];
        case 'contacts': return [d('Account', s(r.AccountName), r.AccountId ? `/accounts/${r.AccountId}` : undefined), d('Job title', s(r.JobTitle)), d('Email', s(r.Email)), d('Phone', s(r.Phone)), d('Mobile', s(r.Mobile))];
        case 'opportunities': {
            const amount = num(r.Amount);
            return [d('Account', s(r.AccountName), `/accounts/${r.AccountId}`), d('Primary contact', s(r.PrimaryContactName), r.PrimaryContactId ? `/contacts/${r.PrimaryContactId}` : undefined), d('Stage', s(r.StageName)), d('Probability', `${s(r.Probability)}%`),
                d('Amount', f.money(amount, s(r.CurrencyCode))), d('Weighted amount', f.money(Math.round(amount * num(r.Probability)) / 100, s(r.CurrencyCode))), d('Close date', f.date(s(r.CloseDate))), d('Lead source', s(r.LeadSourceLabel)),
                ...(r.StageType === 'lost' ? [d('Lost reason', s(r.LostReasonLabel))] : [])];
        }
        case 'activities': return [d('Type', exports.TYPE_LABEL[s(r.ActivityType)] ?? ''), d('Status', r.IsDone ? 'Done' : 'Open'), d('Related to', relatedTo(r).text, relatedTo(r).href), d('Due', f.date(s(r.DueDate)) + (r.DueTime ? ` ${s(r.DueTime)}` : '')), d('Description', s(r.Description))];
    }
}
const FIELD_LABEL = { OwnerId: 'Owner', StageId: 'Stage', CurrencyCode: 'Currency', CloseDate: 'Close date', Amount: 'Amount' };
/** One readable sentence per audit row, e.g. "Owner changed from Anna Teammate to Demo User". */
function describeHistory(h, f) {
    if (h.action === 'C')
        return 'Created';
    if (h.action === 'D')
        return 'Deleted';
    const label = FIELD_LABEL[h.field ?? ''] ?? h.field ?? 'Field';
    const show = (v) => (v === null ? '(empty)' : h.field === 'CloseDate' ? f.date(v) : v);
    return `${label} changed from ${show(h.oldValue)} to ${show(h.newValue)}`;
}
function canDelete(user, r) {
    return user.role === 'Admin' || r.OwnerId === user.userId || (user.role === 'Manager' && user.teamId !== null && r.TeamId === user.teamId);
}
const opt = (o) => o.map((x) => ({ value: String(x.id), label: x.label }));
const emptyOpt = (label) => ({ value: '', label });
function filterFields(entity, q, lk, owners, admin) {
    const f = (name, label, options) => ({ name, label, options: [emptyOpt('All'), ...options], value: q.filters[name] ?? '' });
    const ownerF = f('owner', 'Owner', owners.map((o) => ({ value: String(o.id), label: ownerLabel(o, admin) })));
    const picks = (code) => (lk.picklists[code] ?? []).map((p) => ({ value: String(p.id), label: p.label }));
    switch (entity) {
        case 'accounts': return [ownerF, f('industry', 'Industry', picks('industry'))];
        case 'contacts': return [ownerF];
        case 'opportunities': return [f('stage', 'Stage', lk.stages.map((st) => ({ value: String(st.id), label: st.name }))), ownerF,
            { name: 'status', label: 'Status', value: q.filters.status ?? 'open', options: ['open', 'won', 'lost', 'closed', 'all'].map((v) => ({ value: v, label: cap(v) })) }];
        case 'activities': return [f('type', 'Type', [...Object.entries(exports.TYPE_LABEL).map(([value, label]) => ({ value, label })), { value: 'actionable', label: 'Call, meeting or task' }]), ownerF,
            { name: 'status', label: 'Status', value: q.filters.status ?? 'open', options: ['open', 'done', 'all'].map((v) => ({ value: v, label: cap(v) })) },
            { name: 'due', label: 'Due', value: q.filters.due ?? 'any', options: [{ value: 'any', label: 'Any' }, { value: 'due', label: 'Due or overdue' }, { value: 'overdue', label: 'Overdue' }, { value: 'today', label: 'Today' }, { value: 'week', label: 'Next 7 days' }] }];
    }
}
/** Raw form strings for an existing record (what the user would see in the inputs). */
function formValuesFromRow(entity, r, f) {
    const out = {};
    for (const c of entities_1.ENTITIES[entity].columns) {
        const v = r[c.name];
        out[c.name] = c.type === 'money' && typeof v === 'number' ? f.amountInput(v) : c.type === 'bool' ? (v ? 'on' : '') : s(v);
    }
    out.OwnerId = s(r.OwnerId);
    return out;
}
function formFields(entity, values, errors, env) {
    const admin = env.role === 'Admin';
    const base = (name, label, type, extra = {}) => ({ name, label, type, value: values[name] ?? '', error: errors[name], ...extra });
    const req = (label) => `${label} (required)`;
    const select = (name, label, options, extra = {}) => {
        const v = values[name] ?? '';
        return base(name, label, 'select', { options: [emptyOpt('— none —'), ...options].map((o) => ({ ...o, selected: o.value === v })), ...extra });
    };
    const picks = (code, current) => (env.lookups.picklists[code] ?? []).filter((p) => p.active || String(p.id) === current).map((p) => ({ value: String(p.id), label: p.active ? p.label : `${p.label} (inactive)` }));
    const ownerOptions = env.owners.map((o) => ({ value: String(o.id), label: ownerLabel(o, admin) }));
    if (env.currentOwner && !env.owners.some((o) => o.id === env.currentOwner.id)) {
        ownerOptions.push({ value: String(env.currentOwner.id), label: `${env.currentOwner.name} (${env.currentOwner.active ? 'other team' : 'inactive'})` });
    }
    const ownerValue = values.OwnerId ?? '';
    const owner = base('OwnerId', req('Owner'), 'select', {
        required: true, options: ownerOptions.map((o) => ({ ...o, selected: o.value === ownerValue })),
        help: admin ? undefined : env.currentOwner && !env.currentOwner.active ? 'Current owner is inactive: choose a new owner.' : 'You can choose people in your team.',
    });
    switch (entity) {
        case 'accounts': return [
            base('Name', req('Name'), 'text', { required: true }), base('Website', 'Website', 'text', { autocomplete: 'url' }), base('Phone', 'Phone', 'tel', { autocomplete: 'tel' }),
            select('IndustryValueId', 'Industry', picks('industry', values.IndustryValueId ?? '')), base('Street', 'Street', 'text'), base('PostalCode', 'Postcode', 'text'), base('City', 'City', 'text'),
            base('CountryCode', 'Country code (two letters)', 'text'), base('Notes', 'Notes', 'textarea'), owner
        ];
        case 'contacts': return [
            base('FirstName', 'First name', 'text'), base('LastName', req('Last name'), 'text', { required: true }), select('AccountId', 'Account', opt(env.refs.accounts)),
            base('JobTitle', 'Job title', 'text'), base('Email', 'Email', 'email', { autocomplete: 'email' }), base('Phone', 'Phone', 'tel'), base('Mobile', 'Mobile', 'tel'), owner
        ];
        case 'opportunities': {
            const stageValue = values.StageId ?? '';
            return [
                base('Name', req('Name'), 'text', { required: true }),
                { ...select('AccountId', req('Account'), opt(env.refs.accounts), { required: true }) },
                select('PrimaryContactId', 'Primary contact', opt(env.refs.contacts)),
                { ...base('StageId', req('Stage'), 'select', { required: true, options: env.lookups.stages.map((st) => ({ value: String(st.id), label: `${st.name} (${st.probability}%)`, selected: String(st.id) === stageValue })) }) },
                base('Amount', req('Amount'), 'text', { required: true, inputmode: 'decimal' }),
                base('CurrencyCode', req('Currency'), 'select', { required: true, options: [...env.lookups.settings.enabledCurrencies, ...((values.CurrencyCode ?? '') && !env.lookups.settings.enabledCurrencies.includes(values.CurrencyCode ?? '') ? [values.CurrencyCode ?? ''] : [])].map((c) => ({ value: c, label: env.lookups.settings.enabledCurrencies.includes(c) ? c : `${c} (disabled)`, selected: c === (values.CurrencyCode ?? '') })) }),
                base('CloseDate', req('Close date'), 'date', { required: true }),
                select('LeadSourceValueId', 'Lead source', picks('lead_source', values.LeadSourceValueId ?? '')),
                select('LostReasonValueId', 'Lost reason (required if the stage is Closed lost)', picks('lost_reason', values.LostReasonValueId ?? '')),
                owner
            ];
        }
        case 'activities': return [
            base('ActivityType', req('Type'), 'radio', { required: true, options: Object.entries(exports.TYPE_LABEL).map(([value, label]) => ({ value, label, selected: value === (values.ActivityType ?? '') })) }),
            base('Subject', req('Subject'), 'text', { required: true }),
            select('AccountId', 'Related account', opt(env.refs.accounts), { group: 'Related to (link at least one)' }),
            select('ContactId', 'Related contact', opt(env.refs.contacts), { group: 'Related to (link at least one)' }),
            select('OpportunityId', 'Related opportunity', opt(env.refs.opportunities), { group: 'Related to (link at least one)' }),
            base('DueDate', 'Due date (not used for notes)', 'date'), base('DueTime', 'Due time', 'time'),
            base('IsDone', 'Done', 'checkbox', { value: values.IsDone ?? '' }), base('Description', 'Description', 'textarea'), owner
        ];
    }
}
function errorList(fields, errors) {
    const order = fields.map((x) => x.name);
    return Object.entries(errors).sort((a, b) => order.indexOf(a[0]) - order.indexOf(b[0])).map(([field, message]) => ({ field, message }));
}
function pageHref(basePath, q, over) {
    const p = new URLSearchParams();
    const all = { q: q.q, sort: q.sort, dir: q.dir, pageSize: q.pageSize, page: q.page, ...q.filters, ...over };
    for (const [k, v] of Object.entries(all))
        if (v !== undefined && v !== '' && v !== null)
            p.set(k, String(v));
    const qs = p.toString();
    return qs ? `${basePath}?${qs}` : basePath;
}
//# sourceMappingURL=crmViews.js.map