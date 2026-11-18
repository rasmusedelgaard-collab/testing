import { ENTITIES, type EntityDef } from '../domain/entities';
import type { EntityKey, HistoryRow, ListQuery, Lookups, OwnerOption, RefOption, Row, Value } from '../domain/types';
import type { Errors } from '../domain/validators';
import type { Formatter } from '../services/format';
import type { Role } from '../auth/types';

/** Pure view-model builders: everything the EJS templates need, already formatted and escaped-safe (templates escape). */
export interface Cell { text: string; href?: string; right?: boolean; note?: string; doneForm?: { action: string; context: string; checked: boolean; returnTo: string } }
export interface Column { label: string; sort?: string }
export interface Option { value: string; label: string; selected?: boolean }
export interface FormField {
  name: string; label: string; type: 'text' | 'textarea' | 'select' | 'radio' | 'date' | 'time' | 'checkbox' | 'tel' | 'email';
  required?: boolean; value: string; error?: string; help?: string; options?: Option[]; inputmode?: string; autocomplete?: string; group?: string;
}
export interface FilterField { name: string; label: string; options: Option[]; value: string }

const s = (v: Value | undefined) => (v === null || v === undefined ? '' : String(v));
const num = (v: Value | undefined) => (typeof v === 'number' ? v : Number(v));
export const TYPE_LABEL: Record<string, string> = { call: 'Call', meeting: 'Meeting', task: 'Task', note: 'Note' };
const cap = (x: string) => x.charAt(0).toUpperCase() + x.slice(1);

export function ownerLabel(o: OwnerOption, admin: boolean): string {
  return admin && o.teamName ? `${o.name} (${o.teamName})` : o.name;
}

export function recordName(entity: EntityKey, r: Row): string {
  if (entity === 'contacts') return [s(r.FirstName), s(r.LastName)].filter(Boolean).join(' ');
  return s(r[ENTITIES[entity].labelCol === 'Subject' ? 'Subject' : ENTITIES[entity].labelCol === 'LastName' ? 'LastName' : 'Name']);
}

export function relatedTo(r: Row): Cell {
  if (r.OpportunityId) return { text: s(r.OpportunityName) || 'Opportunity', href: `/opportunities/${r.OpportunityId}` };
  if (r.ContactId) return { text: s(r.ContactName) || 'Contact', href: `/contacts/${r.ContactId}` };
  if (r.AccountId) return { text: s(r.AccountName) || 'Account', href: `/accounts/${r.AccountId}` };
  return { text: '(deleted record)' };
}

export function columnsFor(entity: EntityKey): Column[] {
  switch (entity) {
    case 'accounts': return [{ label: 'Name', sort: 'name' }, { label: 'Industry', sort: 'industry' }, { label: 'City', sort: 'city' }, { label: 'Owner', sort: 'owner' }];
    case 'contacts': return [{ label: 'Name', sort: 'name' }, { label: 'Account', sort: 'account' }, { label: 'Title', sort: 'title' }, { label: 'Email', sort: 'email' }, { label: 'Phone' }, { label: 'Owner', sort: 'owner' }];
    case 'opportunities': return [{ label: 'Name', sort: 'name' }, { label: 'Account', sort: 'account' }, { label: 'Stage', sort: 'stage' }, { label: 'Amount', sort: 'amount' }, { label: 'Close date', sort: 'close' }, { label: 'Owner', sort: 'owner' }];
    case 'activities': return [{ label: 'Done' }, { label: 'Type', sort: 'type' }, { label: 'Subject', sort: 'subject' }, { label: 'Related to' }, { label: 'Due date', sort: 'due' }, { label: 'Owner', sort: 'owner' }];
  }
}

export function cellsFor(entity: EntityKey, r: Row, f: Formatter, today: string, returnTo: string): Cell[] {
  const path = ENTITIES[entity].path;
  const link = `${path}/${r.Id}`;
  switch (entity) {
    case 'accounts': return [{ text: s(r.Name), href: link }, { text: s(r.IndustryLabel) }, { text: s(r.City) }, { text: s(r.OwnerName) }];
    case 'contacts': return [{ text: recordName(entity, r), href: link }, { text: s(r.AccountName), href: r.AccountId ? `/accounts/${r.AccountId}` : undefined }, { text: s(r.JobTitle) }, { text: s(r.Email) }, { text: s(r.Phone) }, { text: s(r.OwnerName) }];
    case 'opportunities': {
      const past = r.StageType === 'open' && s(r.CloseDate) < today;
      return [{ text: s(r.Name), href: link }, { text: s(r.AccountName), href: `/accounts/${r.AccountId}` }, { text: s(r.StageName) },
        { text: f.money(num(r.Amount), s(r.CurrencyCode)), right: true }, { text: f.date(s(r.CloseDate)), note: past ? 'Past close date' : undefined }, { text: s(r.OwnerName) }];
    }
    case 'activities': {
      const done = Boolean(r.IsDone);
      return [{ text: done ? 'Done' : 'Open', doneForm: { action: `${path}/${r.Id}/${done ? 'undone' : 'done'}`, context: done ? `${s(r.Subject)}, mark as not done` : s(r.Subject), checked: done, returnTo } },
        { text: TYPE_LABEL[s(r.ActivityType)] ?? s(r.ActivityType) }, { text: s(r.Subject), href: link }, relatedTo(r),
        { text: f.date(s(r.DueDate)) + (r.DueTime ? ` ${s(r.DueTime)}` : '') }, { text: s(r.OwnerName) }];
    }
  }
}

export function detailRows(entity: EntityKey, r: Row, f: Formatter): Array<{ label: string; text: string; href?: string }> {
  const d = (label: string, text: string, href?: string) => ({ label, text, href });
  switch (entity) {
    case 'accounts': return [d('Industry', s(r.IndustryLabel)), d('Website', s(r.Website)), d('Phone', s(r.Phone)), d('Street', s(r.Street)), d('Postcode', s(r.PostalCode)), d('City', s(r.City)), d('Country', s(r.CountryCode)), d('Notes', s(r.Notes))];
    case 'contacts': return [d('Account', s(r.AccountName), r.AccountId ? `/accounts/${r.AccountId}` : undefined), d('Job title', s(r.JobTitle)), d('Email', s(r.Email)), d('Phone', s(r.Phone)), d('Mobile', s(r.Mobile))];
    case 'opportunities': {
      const amount = num(r.Amount);
      return [d('Account', s(r.AccountName), `/accounts/${r.AccountId}`), d('Primary contact', s(r.PrimaryContactName), r.PrimaryContactId ? `/contacts/${r.PrimaryContactId}` : undefined), d('Stage', s(r.StageName)), d('Probability', `${s(r.Probability)}%`),
        d('Amount', f.money(amount, s(r.CurrencyCode))), d('Weighted amount', f.money(Math.round(amount * num(r.Probability)) / 100, s(r.CurrencyCode))), d('Close date', f.date(s(r.CloseDate))), d('Lead source', s(r.LeadSourceLabel)),
        ...(r.StageType === 'lost' ? [d('Lost reason', s(r.LostReasonLabel))] : [])];
    }
    case 'activities': return [d('Type', TYPE_LABEL[s(r.ActivityType)] ?? ''), d('Status', r.IsDone ? 'Done' : 'Open'), d('Related to', relatedTo(r).text, relatedTo(r).href), d('Due', f.date(s(r.DueDate)) + (r.DueTime ? ` ${s(r.DueTime)}` : '')), d('Description', s(r.Description))];
  }
}

const FIELD_LABEL: Record<string, string> = { OwnerId: 'Owner', StageId: 'Stage', CurrencyCode: 'Currency', CloseDate: 'Close date', Amount: 'Amount' };
/** One readable sentence per audit row, e.g. "Owner changed from Anna Teammate to Demo User". */
export function describeHistory(h: HistoryRow, f: Formatter): string {
  if (h.action === 'C') return 'Created';
  if (h.action === 'D') return 'Deleted';
  const label = FIELD_LABEL[h.field ?? ''] ?? h.field ?? 'Field';
  const show = (v: string | null) => (v === null ? '(empty)' : h.field === 'CloseDate' ? f.date(v) : v);
  return `${label} changed from ${show(h.oldValue)} to ${show(h.newValue)}`;
}

export function canDelete(user: { userId: number; role: Role; teamId: number | null }, r: Row): boolean {
  return user.role === 'Admin' || r.OwnerId === user.userId || (user.role === 'Manager' && user.teamId !== null && r.TeamId === user.teamId);
}

const opt = (o: RefOption[]): Option[] => o.map((x) => ({ value: String(x.id), label: x.label }));
const emptyOpt = (label: string): Option => ({ value: '', label });

export function filterFields(entity: EntityKey, q: ListQuery, lk: Lookups, owners: OwnerOption[], admin: boolean): FilterField[] {
  const f = (name: string, label: string, options: Option[]): FilterField => ({ name, label, options: [emptyOpt('All'), ...options], value: q.filters[name] ?? '' });
  const ownerF = f('owner', 'Owner', owners.map((o) => ({ value: String(o.id), label: ownerLabel(o, admin) })));
  const picks = (code: string) => (lk.picklists[code] ?? []).map((p) => ({ value: String(p.id), label: p.label }));
  switch (entity) {
    case 'accounts': return [ownerF, f('industry', 'Industry', picks('industry'))];
    case 'contacts': return [ownerF];
    case 'opportunities': return [f('stage', 'Stage', lk.stages.map((st) => ({ value: String(st.id), label: st.name }))), ownerF,
      { name: 'status', label: 'Status', value: q.filters.status ?? 'open', options: ['open', 'won', 'lost', 'all'].map((v) => ({ value: v, label: cap(v) })) }];
    case 'activities': return [f('type', 'Type', Object.entries(TYPE_LABEL).map(([value, label]) => ({ value, label }))), ownerF,
      { name: 'status', label: 'Status', value: q.filters.status ?? 'open', options: ['open', 'done', 'all'].map((v) => ({ value: v, label: cap(v) })) },
      { name: 'due', label: 'Due', value: q.filters.due ?? 'any', options: [{ value: 'any', label: 'Any' }, { value: 'overdue', label: 'Overdue' }, { value: 'today', label: 'Today' }, { value: 'week', label: 'This week' }] }];
  }
}

export interface FormEnv {
  lookups: Lookups;
  owners: OwnerOption[];
  refs: { accounts: RefOption[]; contacts: RefOption[]; opportunities: RefOption[] };
  role: Role;
  currentOwner: OwnerOption | null;
  currentOwnerId: number | null;
}

/** Raw form strings for an existing record (what the user would see in the inputs). */
export function formValuesFromRow(entity: EntityKey, r: Row, f: Formatter): Record<string, string> {
  const out: Record<string, string> = {};
  for (const c of ENTITIES[entity].columns) {
    const v = r[c.name];
    out[c.name] = c.type === 'money' && typeof v === 'number' ? f.amountInput(v) : c.type === 'bool' ? (v ? 'on' : '') : s(v);
  }
  out.OwnerId = s(r.OwnerId);
  return out;
}

export function formFields(entity: EntityKey, values: Record<string, string>, errors: Errors, env: FormEnv): FormField[] {
  const admin = env.role === 'Admin';
  const base = (name: string, label: string, type: FormField['type'], extra: Partial<FormField> = {}): FormField => ({ name, label, type, value: values[name] ?? '', error: errors[name], ...extra });
  const req = (label: string) => `${label} (required)`;
  const select = (name: string, label: string, options: Option[], extra: Partial<FormField> = {}): FormField => {
    const v = values[name] ?? '';
    return base(name, label, 'select', { options: [emptyOpt('— none —'), ...options].map((o) => ({ ...o, selected: o.value === v })), ...extra });
  };
  const picks = (code: string, current: string) => (env.lookups.picklists[code] ?? []).filter((p) => p.active || String(p.id) === current).map((p) => ({ value: String(p.id), label: p.active ? p.label : `${p.label} (inactive)` }));

  const ownerOptions: Option[] = env.owners.map((o) => ({ value: String(o.id), label: ownerLabel(o, admin) }));
  if (env.currentOwner && !env.owners.some((o) => o.id === env.currentOwner!.id)) {
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
      base('CountryCode', 'Country code (two letters)', 'text'), base('Notes', 'Notes', 'textarea'), owner];
    case 'contacts': return [
      base('FirstName', 'First name', 'text'), base('LastName', req('Last name'), 'text', { required: true }), select('AccountId', 'Account', opt(env.refs.accounts)),
      base('JobTitle', 'Job title', 'text'), base('Email', 'Email', 'email', { autocomplete: 'email' }), base('Phone', 'Phone', 'tel'), base('Mobile', 'Mobile', 'tel'), owner];
    case 'opportunities': {
      const stageValue = values.StageId ?? '';
      return [
        base('Name', req('Name'), 'text', { required: true }),
        { ...select('AccountId', req('Account'), opt(env.refs.accounts), { required: true }) },
        select('PrimaryContactId', 'Primary contact', opt(env.refs.contacts)),
        { ...base('StageId', req('Stage'), 'select', { required: true, options: env.lookups.stages.map((st) => ({ value: String(st.id), label: `${st.name} (${st.probability}%)`, selected: String(st.id) === stageValue })) }) },
        base('Amount', req('Amount'), 'text', { required: true, inputmode: 'decimal' }),
        base('CurrencyCode', req('Currency'), 'select', { required: true, options: env.lookups.settings.enabledCurrencies.map((c) => ({ value: c, label: c, selected: c === (values.CurrencyCode ?? '') })) }),
        base('CloseDate', req('Close date'), 'date', { required: true }),
        select('LeadSourceValueId', 'Lead source', picks('lead_source', values.LeadSourceValueId ?? '')),
        select('LostReasonValueId', 'Lost reason (required if the stage is Closed lost)', picks('lost_reason', values.LostReasonValueId ?? '')),
        owner];
    }
    case 'activities': return [
      base('ActivityType', req('Type'), 'radio', { required: true, options: Object.entries(TYPE_LABEL).map(([value, label]) => ({ value, label, selected: value === (values.ActivityType ?? '') })) }),
      base('Subject', req('Subject'), 'text', { required: true }),
      select('AccountId', 'Related account', opt(env.refs.accounts), { group: 'Related to (link at least one)' }),
      select('ContactId', 'Related contact', opt(env.refs.contacts), { group: 'Related to (link at least one)' }),
      select('OpportunityId', 'Related opportunity', opt(env.refs.opportunities), { group: 'Related to (link at least one)' }),
      base('DueDate', 'Due date (not used for notes)', 'date'), base('DueTime', 'Due time', 'time'),
      base('IsDone', 'Done', 'checkbox', { value: values.IsDone ?? '' }), base('Description', 'Description', 'textarea'), owner];
  }
}

export function errorList(fields: FormField[], errors: Errors): Array<{ field: string; message: string }> {
  const order = fields.map((x) => x.name);
  return Object.entries(errors).sort((a, b) => order.indexOf(a[0]) - order.indexOf(b[0])).map(([field, message]) => ({ field, message }));
}

export function pageHref(basePath: string, q: ListQuery, over: Record<string, string | number | undefined>): string {
  const p = new URLSearchParams();
  const all: Record<string, string | number | undefined> = { q: q.q, sort: q.sort, dir: q.dir, pageSize: q.pageSize, page: q.page, ...q.filters, ...over };
  for (const [k, v] of Object.entries(all)) if (v !== undefined && v !== '' && v !== null) p.set(k, String(v));
  const qs = p.toString();
  return qs ? `${basePath}?${qs}` : basePath;
}
export { type EntityDef };
