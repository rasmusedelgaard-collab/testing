"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.PERIOD_LABEL = exports.PERIODS = exports.monthOf = void 0;
exports.addDays = addDays;
exports.monthStartPlus = monthStartPlus;
exports.endAfterMonths = endAfterMonths;
exports.monthRange = monthRange;
exports.quarterRange = quarterRange;
exports.fiscalYearRange = fiscalYearRange;
exports.resolvePeriod = resolvePeriod;
exports.monthsBetween = monthsBetween;
const format_1 = require("./format");
/** Calendar arithmetic on 'YYYY-MM-DD' strings (no time zones involved: callers pass "today" in the Settings timezone). */
const pad = (n) => String(n).padStart(2, '0');
const ymd = (d) => `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
const parse = (s) => new Date(`${s}T00:00:00Z`);
function addDays(date, days) {
    const d = parse(date);
    d.setUTCDate(d.getUTCDate() + days);
    return ymd(d);
}
/** First day of the month that is `months` after the month of `date`. */
function monthStartPlus(date, months) {
    const d = parse(date);
    return ymd(new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + months, 1)));
}
const monthOf = (date) => date.slice(0, 7);
exports.monthOf = monthOf;
/** Last day of the period that starts on `start` and lasts `months` months. */
function endAfterMonths(start, months) {
    return addDays(monthStartPlus(start, months), -1);
}
function monthRange(today) {
    const from = monthStartPlus(today, 0);
    return { from, to: endAfterMonths(from, 1) };
}
/** Fiscal quarter containing `today`. Quarters start in the fiscal start month and every 3 months after it. */
function quarterRange(today, fiscalStartMonth) {
    const m = Number(today.slice(5, 7)); // 1-12
    const offset = (m - fiscalStartMonth + 12) % 12;
    const monthsBack = offset % 3;
    const from = monthStartPlus(today, -monthsBack);
    return { from, to: endAfterMonths(from, 3) };
}
function fiscalYearRange(today, fiscalStartMonth) {
    const m = Number(today.slice(5, 7));
    const monthsBack = (m - fiscalStartMonth + 12) % 12;
    const from = monthStartPlus(today, -monthsBack);
    return { from, to: endAfterMonths(from, 12) };
}
exports.PERIODS = ['this-month', 'last-month', 'this-quarter', 'last-quarter', 'this-year', 'last-12-months', 'custom'];
exports.PERIOD_LABEL = {
    'this-month': 'This month', 'last-month': 'Last month', 'this-quarter': 'This quarter', 'last-quarter': 'Last quarter', 'this-year': 'This year', 'last-12-months': 'Last 12 months', custom: 'Custom',
};
/** Resolves the Analytics period. Invalid custom ranges fall back to this quarter and report the exact UI copy. */
function resolvePeriod(input, today, fiscalStartMonth) {
    let key = exports.PERIODS.includes(input.period ?? '') ? input.period : 'this-quarter';
    // The page pre-fills From/To with the preset's range and remembers which preset it filled them from (`shown`). If the
    // preset is unchanged but the dates were edited, the user means a custom range. If the preset itself was changed, the
    // old pre-filled dates are stale and ignored.
    if (key !== 'custom' && input.shown === key && (input.from || input.to)) {
        const preset = resolvePeriod({ period: key }, today, fiscalStartMonth).range;
        if (input.from !== preset.from || input.to !== preset.to)
            key = 'custom';
    }
    switch (key) {
        case 'this-month': return { ok: true, key, range: monthRange(today) };
        case 'last-month': return { ok: true, key, range: monthRange(monthStartPlus(today, -1)) };
        case 'this-quarter': return { ok: true, key, range: quarterRange(today, fiscalStartMonth) };
        case 'last-quarter': return { ok: true, key, range: quarterRange(monthStartPlus(today, -3), fiscalStartMonth) };
        case 'this-year': return { ok: true, key, range: fiscalYearRange(today, fiscalStartMonth) };
        case 'last-12-months': return { ok: true, key, range: { from: monthStartPlus(today, -11), to: endAfterMonths(monthStartPlus(today, -11), 12) } };
        case 'custom': {
            const fallback = quarterRange(today, fiscalStartMonth);
            const { from = '', to = '' } = input;
            if (!(0, format_1.isRealDate)(from) || !(0, format_1.isRealDate)(to))
                return { ok: false, key, range: fallback, error: 'Enter a real date, for example 31.12.2026.' };
            if (to < from)
                return { ok: false, key, range: fallback, error: 'End date must be on or after start date.' };
            if (to > addDays(monthStartPlus(from, 0), 366 * 5 + 2))
                return { ok: false, key, range: fallback, error: 'Choose a range of 5 years or less.' };
            return { ok: true, key, range: { from, to } };
        }
    }
}
/** Every 'YYYY-MM' from the month of `from` to the month of `to` (at most `max`, newest dropped beyond that). */
function monthsBetween(from, to, max = 60) {
    const out = [];
    for (let m = monthStartPlus(from, 0); m <= to && out.length < max; m = monthStartPlus(m, 1))
        out.push((0, exports.monthOf)(m));
    return out;
}
//# sourceMappingURL=periods.js.map