import { describe, expect, it } from 'vitest';
import { buildTokenHeatGrid, heatLevel, tokenHeatGridStats, tokenHeatmapDays, tokenHeatmapQuery, tokenHeatmapSpecForHours, tokenHeatmapStats, tokenHeatmapWindow } from './tokenHeatmap';
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

  it('picks minutes, hours, or calendar days from the page window', () => {
    expect(tokenHeatmapSpecForHours(1)).toEqual({ kind: 'minutes', minutes: 60 });
    expect(tokenHeatmapSpecForHours(24)).toEqual({ kind: 'hours', hours: 24 });
    expect(tokenHeatmapSpecForHours(168)).toEqual({ kind: 'hours', hours: 168 });
    expect(tokenHeatmapSpecForHours(720)).toEqual({ kind: 'calendar', days: 30 });
    expect(tokenHeatmapSpecForHours(8760)).toEqual({ kind: 'calendar', days: 365 });
  });
  it('lays out hours as UTC day rows of 24 columns, padding outside the window and summing buckets', () => {
    const now = new Date('2026-01-02T05:30:00Z');
    const spec = { kind: 'hours', hours: 24 } as const;
    expect(tokenHeatmapQuery(spec, now)).toEqual({ fromUtc: '2026-01-01T05:00:00.000Z', toUtc: '2026-01-02T05:30:00.000Z', bucketMinutes: 60 });
    const data = { buckets: [
      { bucketStartUtc: '2026-01-01T06:00:00Z', inputTokens: 10, outputTokens: 2, cachedTokens: 4, totalTokens: 12 },
      { bucketStartUtc: '2026-01-01T06:00:00.000Z', inputTokens: 1, outputTokens: 1, cachedTokens: 0, totalTokens: 2 },
    ] } as TokenUsageSummaryResult;
    const grid = buildTokenHeatGrid(data, spec, 'total', now);
    expect(grid.columns).toBe(24);
    expect(grid.rows.map(r => r.key)).toEqual(['2026-01-01T00:00:00.000Z', '2026-01-02T00:00:00.000Z']);
    expect(grid.rows[0].cells[4]).toBeNull();
    expect(grid.rows[0].cells[6]?.count).toBe(14);
    expect(grid.rows[1].cells[5]?.count).toBe(0);
    expect(grid.rows[1].cells[6]).toBeNull();
    expect(buildTokenHeatGrid(data, spec, 'cached', now).rows[0].cells[6]?.count).toBe(4);
    expect(tokenHeatGridStats(grid)).toEqual({ total: 14, peak: 14, active: 1 });
  });
  it('lays out minutes as UTC hour rows of 60 columns', () => {
    const grid = buildTokenHeatGrid(null, { kind: 'minutes', minutes: 60 }, 'total', new Date('2026-01-02T05:30:20Z'));
    expect(grid.bucketMinutes).toBe(1);
    expect(grid.columns).toBe(60);
    expect(grid.rows).toHaveLength(2);
    expect(grid.rows.flatMap(r => r.cells).filter(Boolean)).toHaveLength(61);
  });
});
