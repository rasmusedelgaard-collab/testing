import { z } from 'zod';
import { parseAmount, isRealDate } from '../services/format';
import type { EntityKey, Lookups, Values } from './types';

export type Errors = Record<string, string>;
export interface ValidationResult { values: Values; errors: Errors }

const s = (v: unknown) => (typeof v === 'string' ? v.trim() : '');
const req = (max: number, msgReq: string, msgMax: string) => z.preprocess(s, z.string().min(1, msgReq).max(max, msgMax));
const opt = (max: number, msgMax: string, pattern?: RegExp, msgPattern?: string) =>
  z.preprocess(s, z.string().max(max, msgMax).refine((v) => v === '' || !pattern || pattern.test(v), msgPattern ?? 'Invalid value')).transform((v) => (v === '' ? null : v));
const INT_MAX = 2_147_483_647;
const reqInt = (msgReq: string) => z.preprocess(s, z.string().min(1, msgReq).regex(/^[1-9]\d{0,9}$/, msgReq).refine((v) => Number(v) <= INT_MAX, msgReq)).transform(Number);
const optInt = (msg: string) => z.preprocess(s, z.string().regex(/^([1-9]\d{0,9})?$/, msg).refine((v) => v === '' || Number(v) <= INT_MAX, msg)).transform((v) => (v === '' ? null : Number(v)));
const date = (msgReq: string, msgBad: string) => z.preprocess(s, z.string().min(1, msgReq).refine(isRealDate, msgBad));
const optDate = (msgBad: string) => z.preprocess(s, z.string().refine((v) => v === '' || isRealDate(v), msgBad)).transform((v) => (v === '' ? null : v));

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;
const PHONE = /^[0-9+()\-./\s]{5,40}$/;
const WEBSITE = /^(https?:\/\/)?[^\s/$.?#]+(\.[^\s/$.?#]+)+(\/\S*)?$/i;
const phoneMsg = 'Enter a valid phone number.';
const chooseFromList = 'Choose a value from the list.';
const DUE_TIME = /^([01]\d|2[0-3]):[0-5]\d$/;

function shape(entity: EntityKey, locale: string, currencies: string[]): z.ZodRawShape {
  const owner = { OwnerId: reqInt('Choose an owner.') };
  switch (entity) {
    case 'accounts':
      return {
        Name: req(120, 'Enter an account name.', 'Name must be 120 characters or fewer.'),
        Website: opt(255, 'Web address is too long.', WEBSITE, 'Enter a web address like example.com.'),
        Phone: opt(40, phoneMsg, PHONE, phoneMsg),
        IndustryValueId: optInt(chooseFromList),
        Street: opt(200, 'Street must be 200 characters or fewer.'),
        PostalCode: opt(20, 'Postcode must be 20 characters or fewer.'),
        City: opt(100, 'City must be 100 characters or fewer.'),
        CountryCode: opt(2, 'Enter a two-letter country code.', /^[A-Za-z]{2}$/, 'Enter a two-letter country code.').transform((v) => (v ? v.toUpperCase() : null)),
        Notes: opt(2000, 'Notes must be 2000 characters or fewer.'),
        ...owner,
      };
    case 'contacts':
      return {
        AccountId: optInt('Choose an account from the list.'),
        FirstName: opt(80, 'First name must be 80 characters or fewer.'),
        LastName: req(80, 'Enter a last name.', 'Last name must be 80 characters or fewer.'),
        JobTitle: opt(120, 'Job title must be 120 characters or fewer.'),
        Email: opt(254, 'Enter an email address like name@example.com.', EMAIL, 'Enter an email address like name@example.com.'),
        Phone: opt(40, phoneMsg, PHONE, phoneMsg),
        Mobile: opt(40, phoneMsg, PHONE, phoneMsg),
        ...owner,
      };
    case 'opportunities':
      return {
        Name: req(120, 'Enter an opportunity name.', 'Name must be 120 characters or fewer.'),
        AccountId: reqInt('Choose an account.'),
        PrimaryContactId: optInt('Choose a contact from the list.'),
        StageId: reqInt('Choose a stage.'),
        Amount: z.preprocess(s, z.string().min(1, 'Enter an amount, for example 12.500,00.')
          .refine((v) => v === '' || parseAmount(v, locale).ok || (parseAmount(v, locale) as { reason?: string }).reason !== 'negative', "Amount can't be negative.")
          .refine((v) => v === '' || parseAmount(v, locale).ok, 'Enter an amount, for example 12.500,00.')).transform((v) => (parseAmount(v, locale) as { value: number }).value),
        CurrencyCode: z.preprocess((v) => s(v).toUpperCase(), z.string().refine((v) => currencies.includes(v), 'Choose a currency.')),
        CloseDate: date('Enter a close date.', 'Enter a real date, for example 31.12.2026.'),
        LeadSourceValueId: optInt(chooseFromList),
        LostReasonValueId: optInt(chooseFromList),
        ...owner,
      };
    case 'activities':
      return {
        ActivityType: z.preprocess(s, z.enum(['call', 'meeting', 'task', 'note'], { error: 'Choose a type.' })),
        Subject: req(160, 'Enter a subject.', 'Subject must be 160 characters or fewer.'),
        Description: opt(4000, 'Description must be 4000 characters or fewer.'),
        AccountId: optInt('Choose an account from the list.'),
        ContactId: optInt('Choose a contact from the list.'),
        OpportunityId: optInt('Choose an opportunity from the list.'),
        DueDate: optDate('Enter a real date, for example 31.12.2026.'),
        DueTime: opt(5, 'Enter a time like 14:30.', DUE_TIME, 'Enter a time like 14:30.'),
        IsDone: z.preprocess((v) => v === 'on' || v === 'true' || v === '1', z.boolean()),
        ...owner,
      };
  }
}

/** Field-level rules (zod) plus cross-field and lookup rules. Owner membership is checked by the route. */
export function validateEntity(entity: EntityKey, body: Record<string, unknown>, lookups: Lookups): ValidationResult {
  const { settings, stages, picklists } = lookups;
  const parsed = z.object(shape(entity, settings.localeCode, settings.enabledCurrencies)).safeParse(body);
  const errors: Errors = {};
  if (!parsed.success) {
    for (const issue of parsed.error.issues) {
      const key = String(issue.path[0] ?? '');
      if (key && !errors[key]) errors[key] = issue.message;
    }
    return { values: {}, errors };
  }
  const v = parsed.data as Values;
  const activeIn = (code: string, id: unknown) => id === null || (picklists[code] ?? []).some((p) => p.active && p.id === id);
  const check = (field: string, code: string) => {
    if (!activeIn(code, v[field] ?? null)) errors[field] = chooseFromList;
  };
  if (entity === 'accounts') check('IndustryValueId', 'industry');
  if (entity === 'opportunities') {
    check('LeadSourceValueId', 'lead_source');
    const stage = stages.find((st) => st.id === v.StageId);
    if (!stage) errors.StageId = 'Choose a stage.';
    if (stage?.type === 'lost') {
      if (v.LostReasonValueId === null) errors.LostReasonValueId = 'Choose why this was lost.';
      else check('LostReasonValueId', 'lost_reason');
    } else {
      v.LostReasonValueId = null;
    }
  }
  if (entity === 'activities') {
    if (v.AccountId === null && v.ContactId === null && v.OpportunityId === null) errors.AccountId = 'Link this activity to an account, contact or opportunity.';
    if (v.ActivityType === 'note') {
      v.DueDate = null;
      v.DueTime = null;
    } else if (v.DueTime !== null && v.DueDate === null) {
      errors.DueDate = 'Enter a due date for this time.';
    }
  }
  return { values: v, errors };
}
