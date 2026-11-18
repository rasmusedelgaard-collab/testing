"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.rateExample = exports.amountExample = exports.parseRate = exports.parseAmount = exports.ALLOWED_LOCALES = void 0;
exports.makeFormatter = makeFormatter;
exports.todayIn = todayIn;
exports.parseDecimal = parseDecimal;
exports.rateInput = rateInput;
exports.isRealDate = isRealDate;
exports.ALLOWED_LOCALES = ['da-DK', 'en-GB', 'en-US'];
function safeLocale(code) {
    return exports.ALLOWED_LOCALES.includes(code) ? code : 'en-GB';
}
function makeFormatter(s) {
    const locale = safeLocale(s.localeCode);
    let tz = s.timezone;
    try {
        new Intl.DateTimeFormat(locale, { timeZone: tz });
    }
    catch {
        tz = 'UTC';
    }
    const dateFmt = new Intl.DateTimeFormat(locale, { timeZone: 'UTC', year: 'numeric', month: '2-digit', day: '2-digit' });
    const dateTzFmt = new Intl.DateTimeFormat(locale, { timeZone: tz, year: 'numeric', month: '2-digit', day: '2-digit' });
    // 24h "14:02" for da-DK and en-GB (en-GB gives the colon form); en-US keeps its native 12h clock.
    const timeFmt = new Intl.DateTimeFormat(locale === 'en-US' ? 'en-US' : 'en-GB', { timeZone: tz, hour: locale === 'en-US' ? 'numeric' : '2-digit', minute: '2-digit', hourCycle: locale === 'en-US' ? 'h12' : 'h23' });
    return {
        money: (amount, currency) => `${currency} ${new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(amount)}`,
        // Date-only values are calendar dates: format them in UTC so they never shift a day.
        date: (iso) => (iso && /^\d{4}-\d{2}-\d{2}/.test(iso) ? dateFmt.format(new Date(`${iso.slice(0, 10)}T00:00:00Z`)) : ''),
        dateTime: (iso) => (iso ? `${dateTzFmt.format(new Date(iso))} ${timeFmt.format(new Date(iso))}` : ''),
        amountInput: (amount) => new Intl.NumberFormat(locale, { useGrouping: false, minimumFractionDigits: 0, maximumFractionDigits: 2 }).format(amount),
    };
}
function todayIn(timezone, now = new Date()) {
    try {
        return new Intl.DateTimeFormat('en-CA', { timeZone: timezone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(now);
    }
    catch {
        return now.toISOString().slice(0, 10);
    }
}
const escapeRe = (c) => c.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
/**
 * Parses a decimal typed in the Settings locale. Financial data, so it is strict and never guesses:
 *  - a decimal separator is the locale's one (da-DK ",", en-GB/en-US "."), followed by 1..maxDecimals digits;
 *  - the integer part is EITHER plain digits ("1234") OR properly grouped ("1.234.567" in da-DK, "1,234,567" in en):
 *    a group separator is accepted only between groups of exactly 3 digits, with 1-3 digits before the first one;
 *  - anything else is an error. In particular the OTHER locale's separator never silently disappears:
 *    en "6,8" is an error (not 68), da-DK "12.5" is an error (not 125), da-DK "7.4590" is an error (not 74590);
 *  - no spaces, no signs ("-" is reported as 'negative'), no exponent, no empty string.
 * With allowGrouping=false (FX rates) group separators are never accepted.
 */
function parseDecimal(input, localeCode, maxDecimals, allowGrouping = true) {
    const parts = new Intl.NumberFormat(safeLocale(localeCode)).formatToParts(1234567.5);
    const group = parts.find((p) => p.type === 'group')?.value ?? ',';
    const decimal = parts.find((p) => p.type === 'decimal')?.value ?? '.';
    const s = input.trim();
    if (s.startsWith('-'))
        return { ok: false, reason: 'negative' };
    const integer = allowGrouping ? `(?:\\d+|\\d{1,3}(?:${escapeRe(group)}\\d{3})+)` : '\\d+';
    const m = new RegExp(`^(${integer})(?:${escapeRe(decimal)}(\\d{1,${maxDecimals}}))?$`).exec(s);
    if (!m)
        return { ok: false, reason: 'format' };
    const value = Number(`${m[1].split(group).join('')}${m[2] ? `.${m[2]}` : ''}`);
    if (!Number.isFinite(value) || value > 99_999_999_999_999)
        return { ok: false, reason: 'format' };
    return { ok: true, value };
}
const parseAmount = (input, localeCode) => parseDecimal(input, localeCode, 2, true);
exports.parseAmount = parseAmount;
/** FX rates: up to 6 decimals and no group separators at all. */
const parseRate = (input, localeCode) => parseDecimal(input, localeCode, 6, false);
exports.parseRate = parseRate;
/** Examples shown in error messages, in the Settings locale ("12.500,00" / "12,500.00", "7,4590" / "7.4590"). */
const amountExample = (localeCode) => new Intl.NumberFormat(safeLocale(localeCode), { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(12500);
exports.amountExample = amountExample;
const rateExample = (localeCode) => new Intl.NumberFormat(safeLocale(localeCode), { useGrouping: false, minimumFractionDigits: 4, maximumFractionDigits: 4 }).format(7.459);
exports.rateExample = rateExample;
/** Rate for form inputs, locale decimal separator, no grouping. */
function rateInput(rate, localeCode) {
    return new Intl.NumberFormat(safeLocale(localeCode), { useGrouping: false, minimumFractionDigits: 0, maximumFractionDigits: 6 }).format(rate);
}
function isRealDate(s) {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(s))
        return false;
    const d = new Date(`${s}T00:00:00Z`);
    return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === s && Number(s.slice(0, 4)) >= 1900;
}
//# sourceMappingURL=format.js.map