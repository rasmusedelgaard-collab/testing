"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.EMPTY_TEXT = void 0;
exports.pipelineByStage = pipelineByStage;
exports.wonRevenueByMonth = wonRevenueByMonth;
exports.winRateByOwner = winRateByOwner;
exports.activitiesByType = activitiesByType;
exports.oppsByLeadSource = oppsByLeadSource;
const fx_1 = require("./fx");
const periods_1 = require("./periods");
const compact = (c, v) => new Intl.NumberFormat(c.locale, { style: 'currency', currency: c.base, notation: 'compact', minimumFractionDigits: 0, maximumFractionDigits: 1 }).format(v);
const pct = (c, v, digits = 0) => new Intl.NumberFormat(c.locale, { style: 'percent', minimumFractionDigits: digits, maximumFractionDigits: digits }).format(v);
const monthLabel = (ym, locale) => new Intl.DateTimeFormat(locale, { timeZone: 'UTC', month: 'short', year: 'numeric' }).format(new Date(`${ym}-01T00:00:00Z`));
exports.EMPTY_TEXT = 'No data for this period. Try a longer date range.';
const amount = (conv, c) => (conv.excluded.length > 0 && conv.total === 0 ? '–' : c.f.money(conv.total, c.base));
const flagOf = (c) => [c.excluded.length ? 'Not included: no FX rate' : '', c.fallbackCurrencies.length ? 'Latest rate used' : ''].filter(Boolean).join('; ') || undefined;
const base = (id, title, kind, valueKind, seriesLabel) => ({
    id, title, kind, valueKind, seriesLabel, takeaway: exports.EMPTY_TEXT, notes: [], labels: [], data: [], table: { columns: [], rows: [] }, ariaLabel: `${title}: no data`, empty: true,
});
/** Open pipeline per open stage (close date in the period), converted at the current month's rate. */
function pipelineByStage(rows, stages, conv, currentMonth, c) {
    const vm = base('pipeline', 'Pipeline by stage', 'bar', 'money', `Amount in ${c.base}`);
    const open = stages.filter((s) => s.type === 'open').sort((a, b) => a.sortOrder - b.sortOrder);
    const per = open.map((s) => ({ s, conv: (0, fx_1.convertRows)(rows.filter((r) => r.stageId === s.id), conv, () => currentMonth), n: rows.filter((r) => r.stageId === s.id).reduce((x, r) => x + r.count, 0) }));
    const all = (0, fx_1.convertRows)(rows, conv, () => currentMonth);
    vm.notes = [(0, fx_1.exclusionNote)(all), (0, fx_1.fallbackNote)(all)].filter(Boolean);
    vm.labels = per.map((p) => p.s.name);
    vm.data = per.map((p) => p.conv.total);
    vm.table = { columns: ['Stage', 'Opportunities', `Amount (${c.base})`], rows: per.map((p) => ({ cells: [p.s.name, String(p.n), amount(p.conv, c)], flag: flagOf(p.conv) })) };
    if (rows.length === 0)
        return vm;
    const top = [...per].sort((a, b) => b.conv.total - a.conv.total)[0];
    vm.empty = false;
    vm.takeaway = `${top.s.name} holds the most value: ${compact(c, top.conv.total)}`;
    vm.ariaLabel = `Bar chart of open pipeline by stage. ${vm.takeaway.replace(/\.$/, "")}.`;
    return vm;
}
/** Won revenue per close month, each converted at its own close-month rate. */
function wonRevenueByMonth(rows, range, conv, c) {
    const vm = base('won', 'Won revenue by month', 'line', 'money', `Won revenue in ${c.base}`);
    const months = (0, periods_1.monthsBetween)(range.from, range.to);
    const per = months.map((m) => {
        const mr = rows.filter((r) => r.month === m);
        return { m, conv: (0, fx_1.convertRows)(mr, conv, () => m), n: mr.reduce((x, r) => x + r.count, 0) };
    });
    const all = (0, fx_1.convertRows)(rows, conv, (r) => r.month ?? (0, periods_1.monthOf)(range.to));
    vm.notes = [(0, fx_1.exclusionNote)(all), (0, fx_1.fallbackNote)(all)].filter(Boolean);
    vm.labels = months.map((m) => monthLabel(m, c.locale));
    vm.data = per.map((p) => p.conv.total);
    vm.table = { columns: ['Month', 'Deals', `Revenue (${c.base})`], rows: per.map((p) => ({ cells: [monthLabel(p.m, c.locale), String(p.n), amount(p.conv, c)], flag: flagOf(p.conv) })) };
    if (rows.length === 0)
        return vm;
    const best = [...per].sort((a, b) => b.conv.total - a.conv.total)[0];
    vm.empty = false;
    vm.takeaway = `Best month: ${monthLabel(best.m, c.locale)} with ${compact(c, best.conv.total)}`;
    vm.ariaLabel = `Line chart of won revenue by month. ${vm.takeaway.replace(/\.$/, "")}.`;
    return vm;
}
function winRateByOwner(rows, c) {
    const vm = base('winrate', 'Win rate by owner', 'hbar', 'percent', 'Win rate');
    const owners = new Map();
    for (const r of rows) {
        if (r.ownerId === null || r.stageType === 'open')
            continue;
        const o = owners.get(r.ownerId) ?? { name: r.ownerName ?? 'Unknown', won: 0, lost: 0 };
        if (r.stageType === 'won')
            o.won += r.count;
        else
            o.lost += r.count;
        owners.set(r.ownerId, o);
    }
    const list = [...owners.values()].map((o) => ({ ...o, rate: o.won / (o.won + o.lost) })).sort((a, b) => b.rate - a.rate || a.name.localeCompare(b.name));
    vm.labels = list.map((o) => o.name);
    vm.data = list.map((o) => Math.round(o.rate * 1000) / 10);
    vm.table = { columns: ['Owner', 'Won', 'Lost', 'Win rate'], rows: list.map((o) => ({ cells: [o.name, String(o.won), String(o.lost), pct(c, o.rate, 1)] })) };
    if (list.length === 0)
        return vm;
    vm.empty = false;
    vm.takeaway = `${list[0].name} has the highest win rate: ${pct(c, list[0].rate)}`;
    vm.ariaLabel = `Horizontal bar chart of win rate by owner. ${vm.takeaway.replace(/\.$/, "")}.`;
    return vm;
}
const TYPE_ORDER = ['call', 'meeting', 'task', 'note'];
const TYPE_LABEL = { call: 'Call', meeting: 'Meeting', task: 'Task', note: 'Note' };
function activitiesByType(rows, c) {
    const vm = base('activities', 'Activities by type', 'bar', 'count', 'Activities');
    const list = TYPE_ORDER.map((t) => ({ t, n: rows.find((r) => r.type === t)?.count ?? 0 }));
    vm.labels = list.map((x) => TYPE_LABEL[x.t]);
    vm.data = list.map((x) => x.n);
    vm.table = { columns: ['Type', 'Activities'], rows: list.map((x) => ({ cells: [TYPE_LABEL[x.t], new Intl.NumberFormat(c.locale).format(x.n)] })) };
    const total = list.reduce((n, x) => n + x.n, 0);
    if (total === 0)
        return vm;
    const top = [...list].sort((a, b) => b.n - a.n)[0];
    vm.empty = false;
    vm.takeaway = `${TYPE_LABEL[top.t]} is the most common type: ${top.n} of ${total}`;
    vm.ariaLabel = `Bar chart of activities by type. ${vm.takeaway.replace(/\.$/, "")}.`;
    return vm;
}
function oppsByLeadSource(rows, c) {
    const vm = base('leadsource', 'Opportunities by lead source', 'hbar', 'count', 'Opportunities');
    const m = new Map();
    for (const r of rows)
        m.set(r.leadSource ?? '(none)', (m.get(r.leadSource ?? '(none)') ?? 0) + r.count);
    const list = [...m].map(([name, n]) => ({ name, n })).sort((a, b) => b.n - a.n || a.name.localeCompare(b.name));
    vm.labels = list.map((x) => x.name);
    vm.data = list.map((x) => x.n);
    vm.table = { columns: ['Lead source', 'Opportunities'], rows: list.map((x) => ({ cells: [x.name, new Intl.NumberFormat(c.locale).format(x.n)] })) };
    if (list.length === 0)
        return vm;
    vm.empty = false;
    vm.takeaway = `${list[0].name} brings the most opportunities: ${list[0].n}`;
    vm.ariaLabel = `Horizontal bar chart of opportunities by lead source. ${vm.takeaway.replace(/\.$/, "")}.`;
    return vm;
}
//# sourceMappingURL=charts.js.map