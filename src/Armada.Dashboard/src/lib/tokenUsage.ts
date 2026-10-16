/**
 * Token usage ranges. Bucket counts per range: hour = 2/min (120), day = 4/hour (96), week = 12/day (84),
 * month = 4/day (120), 3 months = 1/day (90), 12 months = 1/week (53).
 */
export const TOKEN_USAGE_TIME_RANGES = [
  { label: 'Last Hour', value: 'hour', hours: 1, stepMinutes: 0.5 },
  { label: 'Last Day', value: 'day', hours: 24, stepMinutes: 15 },
  { label: 'Last Week', value: 'week', hours: 168, stepMinutes: 120 },
  { label: 'Last Month', value: 'month', hours: 720, stepMinutes: 360 },
  { label: 'Last 3 months', value: 'quarter', hours: 2160, stepMinutes: 1440 },
  { label: 'Last 12 months', value: 'year', hours: 8760, stepMinutes: 10080 },
] as const;

/** A token count split into parts that add up to the total: input not served from cache, cached input, and output. */
export interface TokenTypeSplit { uncached: number; cached: number; output: number }

/**
 * Split counts by token type. Input already includes cache reads (total = input + output), so cached input is
 * carved out of input rather than added to it, and the three parts sum to the total.
 */
export function splitTokenTypes(input: number, output: number, cached: number): TokenTypeSplit {
  const cachedPart = Math.max(0, Math.min(cached, input));
  return { uncached: Math.max(0, input - cachedPart), cached: cachedPart, output: Math.max(0, output) };
}

export type TokenUsageRangeValue = typeof TOKEN_USAGE_TIME_RANGES[number]['value'];

/** Round axis ticks from 0 to at least `max`. */
export function computeYTicks(max: number): number[] {
  if (max <= 0) return [0];
  const rawStep = max / 4;
  const magnitude = Math.pow(10, Math.floor(Math.log10(rawStep)));
  const normalized = rawStep / magnitude;
  const niceNormalized = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
  const step = niceNormalized * magnitude;
  const ticks: number[] = [];
  for (let value = 0; value <= max; value += step) ticks.push(value);
  if (ticks[ticks.length - 1] < max) ticks.push(ticks[ticks.length - 1] + step);
  return ticks;
}

function trimZero(value: number): string {
  const fixed = value.toFixed(1);
  return fixed.endsWith('.0') ? fixed.slice(0, -2) : fixed;
}

/** Compact token count: 1.2K, 3M, 4.5B. */
export function formatTokens(value: number): string {
  const abs = Math.abs(value);
  if (abs >= 1e9) return trimZero(value / 1e9) + 'B';
  if (abs >= 1e6) return trimZero(value / 1e6) + 'M';
  if (abs >= 1e3) return trimZero(value / 1e3) + 'K';
  return String(Math.round(value));
}
