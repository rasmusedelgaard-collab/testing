"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.makeConverter = makeConverter;
exports.convertRows = convertRows;
exports.exclusionNote = exclusionNote;
exports.fallbackNote = fallbackNote;
/**
 * ADR-0005: rate semantics are 1 CURRENCY = rate BASE. The base currency is always exactly 1 and is never
 * looked up. No rate at or before the month means "not convertible" (the caller must exclude and say so, never use 1:1).
 */
function makeConverter(base, rates) {
    const byCurrency = new Map();
    for (const r of rates) {
        if (r.currency === base || !(r.rate > 0))
            continue;
        const list = byCurrency.get(r.currency) ?? [];
        list.push(r);
        byCurrency.set(r.currency, list);
    }
    for (const list of byCurrency.values())
        list.sort((a, b) => b.month.localeCompare(a.month)); // newest first
    return {
        base,
        convert(amount, currency, month) {
            if (currency === base)
                return { ok: true, value: amount, fallback: false };
            const list = byCurrency.get(currency);
            const hit = list?.find((r) => r.month <= month);
            if (!hit)
                return { ok: false };
            return { ok: true, value: amount * hit.rate, fallback: hit.month !== month };
        },
    };
}
const round2 = (n) => Math.round((n + Number.EPSILON) * 100) / 100;
/** Sums rows in the base currency. `monthOf` picks the rate month per row; `weight` scales a row (e.g. stage probability). */
function convertRows(rows, conv, monthOf, weight = () => 1) {
    let total = 0;
    const excluded = new Map();
    const fallback = new Set();
    for (const r of rows) {
        const c = conv.convert(r.amount * weight(r), r.currency, monthOf(r));
        if (!c.ok) {
            excluded.set(r.currency, (excluded.get(r.currency) ?? 0) + r.count);
            continue;
        }
        total += c.value;
        if (c.fallback)
            fallback.add(r.currency);
    }
    return {
        total: round2(total),
        excluded: [...excluded].map(([currency, count]) => ({ currency, count })).sort((a, b) => a.currency.localeCompare(b.currency)),
        fallbackCurrencies: [...fallback].sort(),
    };
}
/** "3 opportunities in SEK not included: no FX rate" (UI spec 6), or '' when nothing was excluded. */
function exclusionNote(c) {
    if (c.excluded.length === 0)
        return '';
    const parts = c.excluded.map((e) => `${e.count} ${e.count === 1 ? 'opportunity' : 'opportunities'} in ${e.currency}`);
    return `${parts.join(', ')} not included: no FX rate`;
}
function fallbackNote(c) {
    const n = c.fallbackCurrencies.length;
    return n === 0 ? '' : `Uses latest rate for ${n} ${n === 1 ? 'currency' : 'currencies'}`;
}
//# sourceMappingURL=fx.js.map