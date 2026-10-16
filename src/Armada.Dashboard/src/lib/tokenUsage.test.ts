import { describe, expect, it } from 'vitest';
import { splitTokenTypes, TOKEN_USAGE_TIME_RANGES } from './tokenUsage';

describe('token usage helpers', () => {
  it('splits tokens into parts that add up to the total (input already includes cache reads)', () => {
    expect(splitTokenTypes(1000, 200, 600)).toEqual({ uncached: 400, cached: 600, output: 200 });
    const split = splitTokenTypes(1000, 200, 600);
    expect(split.uncached + split.cached + split.output).toBe(1200);
  });
  it('never reports more cached input than input', () => {
    expect(splitTokenTypes(100, 5, 500)).toEqual({ uncached: 0, cached: 100, output: 5 });
    expect(splitTokenTypes(0, 0, 0)).toEqual({ uncached: 0, cached: 0, output: 0 });
  });
  it('offers ranges up to 12 months with bounded bucket counts', () => {
    expect(TOKEN_USAGE_TIME_RANGES.map(r => r.value)).toEqual(['hour', 'day', 'week', 'month', 'quarter', 'year']);
    for (const r of TOKEN_USAGE_TIME_RANGES) expect((r.hours * 60) / r.stepMinutes).toBeLessThanOrEqual(130);
  });
});
