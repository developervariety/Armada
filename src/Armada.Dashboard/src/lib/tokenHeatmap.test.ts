import { describe, expect, it } from 'vitest';
import { heatLevel, tokenHeatmapDays, tokenHeatmapStats, tokenHeatmapWindow } from './tokenHeatmap';
import type { TokenUsageSummaryResult } from '../types/models';

describe('Token calendar', () => {
  it('uses UTC day boundaries across a leap year and does not inherit a chart range', () => {
    const range = tokenHeatmapWindow(365, new Date('2024-03-01T00:10:00Z'));
    expect(range.fromUtc).toBe('2023-03-03T00:00:00.000Z');
    expect(range.to).toBe('2024-03-01');
    expect(range.bucketMinutes).toBe(1440);
  });
  it('fills inactive days and preserves large totals and each token type', () => {
    const data = { buckets: [
      { bucketStartUtc: '2026-01-01T00:00:00Z', inputTokens: 4e9, outputTokens: 25, cachedTokens: 50, totalTokens: 4e9 + 75 },
      { bucketStartUtc: '2026-01-01T12:00:00Z', inputTokens: 5, outputTokens: 5, cachedTokens: 0, totalTokens: 10 },
      { bucketStartUtc: '2025-12-31T23:00:00Z', inputTokens: 999, outputTokens: 0, cachedTokens: 0, totalTokens: 999 },
    ] } as TokenUsageSummaryResult;
    const days = tokenHeatmapDays(data, '2026-01-01', '2026-01-03', 'total');
    expect(days.map(d => d.count)).toEqual([4e9 + 85, 0, 0]);
    expect(tokenHeatmapDays(data, '2026-01-01', '2026-01-03', 'cached')[0].count).toBe(50);
    expect(tokenHeatmapStats(days)).toEqual({ total: 4e9 + 85, peak: 4e9 + 85, active: 1 });
    expect(heatLevel(0, 4e9)).toBe(0);
    expect(heatLevel(4e9, 4e9)).toBe(4);
  });
});
