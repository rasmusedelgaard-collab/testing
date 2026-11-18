import type { AggRow, Stage } from '../domain/types';
export type { AggRow };
import { convertRows, exclusionNote, fallbackNote, type Converted, type Converter } from './fx';
import { addDays, monthOf, monthRange, quarterRange, type Range } from './periods';

export interface Tile {
  key: string;
  label: string;
  /** Base-currency amount, or a plain number/percentage for counts. */
  value: number | null;
  kind: 'money' | 'percent';
  context: string;
  notes: string[];
  /** Currencies whose deals are left out of the value (no FX rate). */
  excludedCurrencies: string[];
  href: string;
}

export const WIN_RATE_DAYS = 90;
export const winRateRange = (today: string): Range => ({ from: addDays(today, -(WIN_RATE_DAYS - 1)), to: today });

export function pipelineTotals(open: AggRow[], stages: Stage[], conv: Converter, currentMonth: string) {
  const prob = new Map(stages.map((s) => [s.id, s.probability]));
  const total = convertRows(open, conv, () => currentMonth);
  const weighted = convertRows(open, conv, () => currentMonth, (r) => (prob.get(r.stageId ?? -1) ?? 0) / 100);
  return { total, weighted, count: open.reduce((n, r) => n + r.count, 0) };
}

/** Win rate = won / (won + lost). null when nothing closed in the window (never a division by zero). */
export function winRate(rows: AggRow[]): { won: number; lost: number; rate: number | null } {
  const won = rows.filter((r) => r.stageType === 'won').reduce((n, r) => n + r.count, 0);
  const lost = rows.filter((r) => r.stageType === 'lost').reduce((n, r) => n + r.count, 0);
  return { won, lost, rate: won + lost === 0 ? null : won / (won + lost) };
}

const notesOf = (c: Converted) => [exclusionNote(c), fallbackNote(c)].filter(Boolean);
const missingOf = (c: Converted) => c.excluded.map((e) => e.currency);
const dealWord = (n: number) => `${n} ${n === 1 ? 'deal' : 'deals'}`;

export interface DashboardInput {
  today: string;
  fiscalStartMonth: number;
  stages: Stage[];
  conv: Converter;
  openRows: AggRow[];
  /** Won rows in the fiscal quarter, grouped by close month. */
  quarterWonRows: AggRow[];
  /** Won and lost rows with close dates in the win-rate window. */
  winRateRows: AggRow[];
}

export function computeTiles(i: DashboardInput): Tile[] {
  const curMonth = monthOf(i.today);
  const month = monthRange(i.today);
  const quarter = quarterRange(i.today, i.fiscalStartMonth);
  const pipe = pipelineTotals(i.openRows, i.stages, i.conv, curMonth);
  const wonQuarterRows = i.quarterWonRows.filter((r) => r.stageType === 'won');
  const wonMonthRows = wonQuarterRows.filter((r) => r.month === curMonth);
  const byCloseMonth = (r: AggRow) => r.month ?? curMonth;
  const wonMonth = convertRows(wonMonthRows, i.conv, byCloseMonth);
  const wonQuarter = convertRows(wonQuarterRows, i.conv, byCloseMonth);
  const wr = winRate(i.winRateRows);
  const win = winRateRange(i.today);
  return [
    { key: 'open', label: 'Open pipeline', value: pipe.total.total, kind: 'money', context: dealWord(pipe.count).replace('deal', 'opp'), notes: notesOf(pipe.total), excludedCurrencies: missingOf(pipe.total), href: '/opportunities?status=open' },
    { key: 'weighted', label: 'Weighted pipeline', value: pipe.weighted.total, kind: 'money', context: 'amount × stage probability', notes: notesOf(pipe.weighted), excludedCurrencies: missingOf(pipe.weighted), href: '/opportunities?status=open' },
    { key: 'won-month', label: 'Won this month', value: wonMonth.total, kind: 'money', context: dealWord(wonMonthRows.reduce((n, r) => n + r.count, 0)), notes: notesOf(wonMonth), excludedCurrencies: missingOf(wonMonth), href: `/opportunities?status=won&from=${month.from}&to=${month.to}` },
    { key: 'won-quarter', label: 'Won this quarter', value: wonQuarter.total, kind: 'money', context: dealWord(wonQuarterRows.reduce((n, r) => n + r.count, 0)), notes: notesOf(wonQuarter), excludedCurrencies: missingOf(wonQuarter), href: `/opportunities?status=won&from=${quarter.from}&to=${quarter.to}` },
    { key: 'win-rate', label: 'Win rate (last 90 d)', value: wr.rate === null ? null : wr.rate * 100, kind: 'percent', context: wr.rate === null ? 'No closed deals yet' : `${wr.won} of ${wr.won + wr.lost}`, notes: [], excludedCurrencies: [], href: `/opportunities?status=closed&from=${win.from}&to=${win.to}` },
  ];
}
