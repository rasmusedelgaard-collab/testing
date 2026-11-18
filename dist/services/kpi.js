"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.winRateRange = exports.WIN_RATE_DAYS = void 0;
exports.pipelineTotals = pipelineTotals;
exports.winRate = winRate;
exports.computeTiles = computeTiles;
const fx_1 = require("./fx");
const periods_1 = require("./periods");
exports.WIN_RATE_DAYS = 90;
const winRateRange = (today) => ({ from: (0, periods_1.addDays)(today, -(exports.WIN_RATE_DAYS - 1)), to: today });
exports.winRateRange = winRateRange;
function pipelineTotals(open, stages, conv, currentMonth) {
    const prob = new Map(stages.map((s) => [s.id, s.probability]));
    const total = (0, fx_1.convertRows)(open, conv, () => currentMonth);
    const weighted = (0, fx_1.convertRows)(open, conv, () => currentMonth, (r) => (prob.get(r.stageId ?? -1) ?? 0) / 100);
    return { total, weighted, count: open.reduce((n, r) => n + r.count, 0) };
}
/** Win rate = won / (won + lost). null when nothing closed in the window (never a division by zero). */
function winRate(rows) {
    const won = rows.filter((r) => r.stageType === 'won').reduce((n, r) => n + r.count, 0);
    const lost = rows.filter((r) => r.stageType === 'lost').reduce((n, r) => n + r.count, 0);
    return { won, lost, rate: won + lost === 0 ? null : won / (won + lost) };
}
const notesOf = (c) => [(0, fx_1.exclusionNote)(c), (0, fx_1.fallbackNote)(c)].filter(Boolean);
const missingOf = (c) => c.excluded.map((e) => e.currency);
const dealWord = (n) => `${n} ${n === 1 ? 'deal' : 'deals'}`;
function computeTiles(i) {
    const curMonth = (0, periods_1.monthOf)(i.today);
    const month = (0, periods_1.monthRange)(i.today);
    const quarter = (0, periods_1.quarterRange)(i.today, i.fiscalStartMonth);
    const pipe = pipelineTotals(i.openRows, i.stages, i.conv, curMonth);
    const wonQuarterRows = i.quarterWonRows.filter((r) => r.stageType === 'won');
    const wonMonthRows = wonQuarterRows.filter((r) => r.month === curMonth);
    const byCloseMonth = (r) => r.month ?? curMonth;
    const wonMonth = (0, fx_1.convertRows)(wonMonthRows, i.conv, byCloseMonth);
    const wonQuarter = (0, fx_1.convertRows)(wonQuarterRows, i.conv, byCloseMonth);
    const wr = winRate(i.winRateRows);
    const win = (0, exports.winRateRange)(i.today);
    return [
        { key: 'open', label: 'Open pipeline', value: pipe.total.total, kind: 'money', context: dealWord(pipe.count).replace('deal', 'opp'), notes: notesOf(pipe.total), excludedCurrencies: missingOf(pipe.total), href: '/opportunities?status=open' },
        { key: 'weighted', label: 'Weighted pipeline', value: pipe.weighted.total, kind: 'money', context: 'amount × stage probability', notes: notesOf(pipe.weighted), excludedCurrencies: missingOf(pipe.weighted), href: '/opportunities?status=open' },
        { key: 'won-month', label: 'Won this month', value: wonMonth.total, kind: 'money', context: dealWord(wonMonthRows.reduce((n, r) => n + r.count, 0)), notes: notesOf(wonMonth), excludedCurrencies: missingOf(wonMonth), href: `/opportunities?status=won&from=${month.from}&to=${month.to}` },
        { key: 'won-quarter', label: 'Won this quarter', value: wonQuarter.total, kind: 'money', context: dealWord(wonQuarterRows.reduce((n, r) => n + r.count, 0)), notes: notesOf(wonQuarter), excludedCurrencies: missingOf(wonQuarter), href: `/opportunities?status=won&from=${quarter.from}&to=${quarter.to}` },
        { key: 'win-rate', label: 'Win rate (last 90 d)', value: wr.rate === null ? null : wr.rate * 100, kind: 'percent', context: wr.rate === null ? 'No closed deals yet' : `${wr.won} of ${wr.won + wr.lost}`, notes: [], excludedCurrencies: [], href: `/opportunities?status=closed&from=${win.from}&to=${win.to}` },
    ];
}
//# sourceMappingURL=kpi.js.map