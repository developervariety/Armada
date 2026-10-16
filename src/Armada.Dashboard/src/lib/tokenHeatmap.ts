import type { TokenUsageSummaryResult } from '../types/models';
import { addDays, buildWeeks, heatLevel } from './vesselHistory';

export type TokenHeatmapMetric = 'total' | 'input' | 'output' | 'cached';
export interface TokenHeatmapDay { date: string; count: number; input: number; output: number; cached: number; total: number }
export const TOKEN_HEATMAP_RANGES = [31, 90, 365] as const;
export const TOKEN_HEATMAP_METRICS: TokenHeatmapMetric[] = ['total', 'input', 'output', 'cached'];

/** UTC calendar days, independent of the monitoring charts' rolling hour/day range. */
export function tokenHeatmapWindow(days: number = 365, now: Date = new Date()) {
  const to = now.toISOString().slice(0, 10);
  const from = addDays(to, -(days - 1));
  return { from, to, fromUtc: `${from}T00:00:00.000Z`, toUtc: now.toISOString(), bucketMinutes: 1440 };
}

/** Fill missing calendar dates, accumulate duplicate daily buckets, and retain exact token counts. */
export function tokenHeatmapDays(data: TokenUsageSummaryResult | null, from: string, to: string, metric: TokenHeatmapMetric): TokenHeatmapDay[] {
  const values = new Map<string, TokenHeatmapDay>();
  for (let date = from; date <= to; date = addDays(date, 1)) values.set(date, { date, count: 0, input: 0, output: 0, cached: 0, total: 0 });
  for (const bucket of data?.buckets ?? []) {
    const day = values.get(bucket.bucketStartUtc.slice(0, 10));
    if (!day) continue;
    day.input += bucket.inputTokens;
    day.output += bucket.outputTokens;
    day.cached += bucket.cachedTokens;
    day.total += bucket.totalTokens;
  }
  return [...values.values()].map(day => ({ ...day, count: day[metric] }));
}

export function tokenHeatmapStats(days: TokenHeatmapDay[]) {
  const total = days.reduce((sum, day) => sum + day.count, 0);
  const peak = Math.max(0, ...days.map(day => day.count));
  const active = days.filter(day => day.count > 0).length;
  return { total, peak, active };
}

export { addDays, buildWeeks, heatLevel };

/**
 * What the heatmap shows: UTC calendar days (weekday rows, week columns), UTC hours (one row per UTC day, 24 hour
 * columns), or minutes (one row per UTC hour, 60 minute columns).
 */
export type TokenHeatmapSpec =
  | { kind: 'calendar'; days: number }
  | { kind: 'hours'; hours: number }
  | { kind: 'minutes'; minutes: number };

/** One heatmap cell: a UTC day, hour, or minute with its exact token counts. */
export interface TokenHeatCell { key: string; startUtc: string; count: number; input: number; output: number; cached: number; total: number }

/** One heatmap row: a weekday (calendar), a UTC day (hours), or a UTC hour (minutes). Null cells are outside the window. */
export interface TokenHeatRow { key: string; cells: Array<TokenHeatCell | null> }

/** A heatmap laid out as rows by columns, with the query that fills it. */
export interface TokenHeatGrid {
  spec: TokenHeatmapSpec;
  fromUtc: string;
  toUtc: string;
  bucketMinutes: number;
  columns: number;
  rows: TokenHeatRow[];
  /** Calendar only: the weeks, for month labels. */
  weeks: ReturnType<typeof buildWeeks>;
}

/** The heatmap for an outer token-usage range: calendar days from a month up, UTC hours for a day or week, minutes for an hour. */
export function tokenHeatmapSpecForHours(hours: number): TokenHeatmapSpec {
  if (hours <= 1) return { kind: 'minutes', minutes: Math.round(hours * 60) };
  if (hours <= 24 * 7) return { kind: 'hours', hours };
  return { kind: 'calendar', days: Math.round(hours / 24) };
}

function emptyCell(key: string, startUtc: string): TokenHeatCell {
  return { key, startUtc, count: 0, input: 0, output: 0, cached: 0, total: 0 };
}

/** The query window for a spec, ending now; hour and minute windows start on a whole UTC hour or minute. */
export function tokenHeatmapQuery(spec: TokenHeatmapSpec, now: Date = new Date()): { fromUtc: string; toUtc: string; bucketMinutes: number } {
  if (spec.kind === 'calendar') {
    const w = tokenHeatmapWindow(spec.days, now);
    return { fromUtc: w.fromUtc, toUtc: w.toUtc, bucketMinutes: 1440 };
  }
  const unit = spec.kind === 'hours' ? 3600000 : 60000;
  const span = spec.kind === 'hours' ? spec.hours * 3600000 : spec.minutes * 60000;
  const from = Math.floor((now.getTime() - span) / unit) * unit;
  return { fromUtc: new Date(from).toISOString(), toUtc: now.toISOString(), bucketMinutes: unit / 60000 };
}

/** Lay out a heatmap for a spec from summary buckets, filling gaps with zero cells and accumulating duplicate buckets. */
export function buildTokenHeatGrid(data: TokenUsageSummaryResult | null, spec: TokenHeatmapSpec, metric: TokenHeatmapMetric, now: Date = new Date()): TokenHeatGrid {
  const query = tokenHeatmapQuery(spec, now);
  if (spec.kind === 'calendar') {
    const w = tokenHeatmapWindow(spec.days, now);
    const days = tokenHeatmapDays(data, w.from, w.to, metric);
    const weeks = buildWeeks(days);
    const rows: TokenHeatRow[] = Array.from({ length: 7 }, (_, weekday) => ({
      key: String(weekday),
      cells: weeks.map(week => {
        const d = week[weekday] as TokenHeatmapDay | null;
        return d ? { key: d.date, startUtc: `${d.date}T00:00:00.000Z`, count: d.count, input: d.input, output: d.output, cached: d.cached, total: d.total } : null;
      }),
    }));
    return { spec, ...query, columns: weeks.length, rows, weeks };
  }

  const hours = spec.kind === 'hours';
  const unit = hours ? 3600000 : 60000;
  const rowUnit = hours ? 86400000 : 3600000;
  const columns = hours ? 24 : 60;
  const keyLength = hours ? 13 : 16;
  const from = Date.parse(query.fromUtc);
  const to = now.getTime();
  const cells = new Map<string, TokenHeatCell>();
  for (let t = from; t <= to; t += unit) {
    const iso = new Date(t).toISOString();
    cells.set(iso.slice(0, keyLength), emptyCell(iso.slice(0, keyLength), iso));
  }
  for (const bucket of data?.buckets ?? []) {
    const cell = cells.get(new Date(bucket.bucketStartUtc).toISOString().slice(0, keyLength));
    if (!cell) continue;
    cell.input += bucket.inputTokens;
    cell.output += bucket.outputTokens;
    cell.cached += bucket.cachedTokens;
    cell.total += bucket.totalTokens;
  }
  for (const cell of cells.values()) cell.count = cell[metric];
  const rows: TokenHeatRow[] = [];
  for (let rowStart = Math.floor(from / rowUnit) * rowUnit; rowStart <= to; rowStart += rowUnit) {
    const rowCells: Array<TokenHeatCell | null> = [];
    for (let c = 0; c < columns; c++) rowCells.push(cells.get(new Date(rowStart + c * unit).toISOString().slice(0, keyLength)) ?? null);
    rows.push({ key: new Date(rowStart).toISOString(), cells: rowCells });
  }
  return { spec, ...query, columns, rows, weeks: [] };
}

/** Total, peak cell, and active cells of a grid. */
export function tokenHeatGridStats(grid: TokenHeatGrid) {
  const cells = grid.rows.flatMap(r => r.cells).filter((c): c is TokenHeatCell => c !== null);
  return { total: cells.reduce((sum, c) => sum + c.count, 0), peak: Math.max(0, ...cells.map(c => c.count)), active: cells.filter(c => c.count > 0).length };
}
