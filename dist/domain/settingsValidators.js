"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.validateLabel = exports.CURRENCIES = void 0;
exports.timezones = timezones;
exports.validateGeneral = validateGeneral;
exports.validateLocale = validateLocale;
exports.validateFx = validateFx;
exports.validateTeam = validateTeam;
exports.validateStages = validateStages;
const color_1 = require("../services/color");
const format_1 = require("../services/format");
exports.CURRENCIES = ['DKK', 'EUR', 'USD', 'GBP', 'SEK', 'NOK', 'CHF', 'PLN', 'CZK', 'HUF', 'ISK', 'CAD', 'AUD', 'JPY', 'CNY', 'INR', 'BRL', 'ZAR', 'SGD', 'AED'];
const s = (v) => (typeof v === 'string' ? v.trim() : '');
function timezones() {
    try {
        return Intl.supportedValuesOf('timeZone');
    }
    catch {
        return ['UTC', 'Europe/Copenhagen'];
    }
}
function validateGeneral(body) {
    const errors = {};
    const appName = s(body.appName);
    if (!appName)
        errors.appName = 'Enter a CRM name.';
    else if (appName.length > 40)
        errors.appName = 'CRM name must be 40 characters or fewer.';
    const accent = s(body.accentColor);
    if (!/^#[0-9a-fA-F]{6}$/.test(accent))
        errors.accentColor = 'Enter a colour like #1D4ED8.';
    else if ((0, color_1.accentContrast)(accent) < 4.5)
        errors.accentColor = 'This colour is too light for text and buttons. Choose a darker shade.';
    return { values: { appName, accentColor: accent.toUpperCase() }, errors };
}
function validateLocale(body) {
    const errors = {};
    const base = s(body.base);
    if (!exports.CURRENCIES.includes(base))
        errors.base = 'Choose a base currency.';
    const raw = body.enabled;
    const enabled = [...new Set((Array.isArray(raw) ? raw : raw ? [raw] : []).map(s).filter((c) => exports.CURRENCIES.includes(c)))];
    if (!errors.base && !enabled.includes(base))
        enabled.unshift(base); // the base currency is always enabled
    const locale = s(body.locale);
    if (!format_1.ALLOWED_LOCALES.includes(locale))
        errors.locale = 'Choose a locale.';
    const timezone = s(body.timezone);
    if (!timezones().includes(timezone))
        errors.timezone = 'Choose a timezone.';
    const firstDay = Number(s(body.firstDay));
    if (!Number.isInteger(firstDay) || firstDay < 1 || firstDay > 7)
        errors.firstDay = 'Choose the first day of the week.';
    const fiscalStart = Number(s(body.fiscalStart));
    if (!Number.isInteger(fiscalStart) || fiscalStart < 1 || fiscalStart > 12)
        errors.fiscalStart = 'Choose a month.';
    return { values: { base, enabled, locale, timezone, firstDay, fiscalStart }, errors };
}
function validateFx(body, o) {
    const errors = {};
    const currency = s(body.currency).toUpperCase();
    if (!currency || currency === o.base || !o.enabled.includes(currency))
        errors.currency = 'Choose a currency.';
    const month = s(body.month);
    if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(month) || Number(month.slice(0, 4)) < 2000)
        errors.month = 'Choose a month.';
    let rate = 0;
    const raw = s(body.rate);
    const r = raw ? (0, format_1.parseRate)(raw, o.locale) : null;
    if (!raw || (r && !r.ok && r.reason === 'format'))
        errors.rate = `Enter a rate, for example ${(0, format_1.rateExample)(o.locale)}.`;
    else if (r && r.ok) {
        rate = r.value;
        if (rate <= 0)
            errors.rate = 'Rate must be more than 0.';
        else if (rate >= 1e10)
            errors.rate = `Enter a rate, for example ${(0, format_1.rateExample)(o.locale)}.`;
    }
    else
        errors.rate = 'Rate must be more than 0.';
    return { values: { currency, month, rate }, errors };
}
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
function validateTeam(body) {
    const errors = {};
    const name = s(body.name);
    if (!name)
        errors.name = 'Enter a team name.';
    else if (name.length > 80)
        errors.name = 'Team name must be 80 characters or fewer.';
    const groupId = s(body.groupId).replace(/[{}\s]/g, '').toLowerCase();
    if (!groupId)
        errors.groupId = 'Enter the Entra group object ID.';
    else if (!GUID.test(groupId))
        errors.groupId = 'Enter the group object ID in this format: 00000000-0000-0000-0000-000000000000.';
    return { values: { name, groupId }, errors };
}
const validateLabel = (v) => {
    const t = s(v);
    if (!t)
        return 'Enter a name.';
    if (t.length > 80)
        return 'Name must be 80 characters or fewer.';
    return null;
};
exports.validateLabel = validateLabel;
/** Rows come from the editor form. Won is forced to 100 and Lost to 0 (the database CHECKs require it). */
function validateStages(rows) {
    const errors = {};
    const summary = [];
    const out = rows.map((r) => ({
        id: /^[1-9]\d{0,9}$/.test(r.id) ? Number(r.id) : null,
        name: s(r.name),
        probability: /^\d{1,3}$/.test(s(r.prob)) ? Number(s(r.prob)) : -1,
        type: r.type === 'won' || r.type === 'lost' ? r.type : 'open',
    }));
    const seen = new Set();
    out.forEach((r, i) => {
        if (!r.name)
            errors[`name${i}`] = 'Enter a stage name.';
        else if (r.name.length > 60)
            errors[`name${i}`] = 'Stage name must be 60 characters or fewer.';
        else if (seen.has(r.name.toLowerCase())) {
            errors[`name${i}`] = 'Each stage needs a different name.';
        }
        seen.add(r.name.toLowerCase());
        if (r.type === 'won')
            r.probability = 100;
        else if (r.type === 'lost')
            r.probability = 0;
        else if (r.probability < 0 || r.probability > 100)
            errors[`prob${i}`] = 'Probability must be between 0 and 100.';
    });
    const won = out.filter((r) => r.type === 'won').length;
    const lost = out.filter((r) => r.type === 'lost').length;
    if (won !== 1 || lost !== 1)
        summary.push('You need exactly one Won stage and one Lost stage.');
    if (!out.some((r) => r.type === 'open'))
        summary.push('You need at least one open stage.');
    return { rows: out, errors, summary };
}
//# sourceMappingURL=settingsValidators.js.map