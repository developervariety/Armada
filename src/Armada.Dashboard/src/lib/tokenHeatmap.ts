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
