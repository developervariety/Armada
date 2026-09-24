import { render, screen } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number>) =>
      (params ? text.replace(/\{\{(\w+)\}\}/g, (_, key: string) => String(params[key] ?? '')) : text),
  };
  return { useLocale: () => locale };
});

vi.mock('../lib/chartImage', () => ({ copySvgToClipboard: vi.fn() }));

vi.mock('../api/client', () => ({
  getTokenUsageSummary: vi.fn(),
}));

import { getTokenUsageSummary } from '../api/client';
import TokenUsage from './TokenUsage';

function breakdown(runtime: string, totalTokens: number) {
  return {
    runtime, model: 'shared-model', sampleCount: 1, missionCount: 1, inputTokens: totalTokens, outputTokens: 0,
    reasoningTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, totalTokens,
  };
}

afterEach(() => {
  vi.restoreAllMocks();
});

test('keeps one model under two runtimes as two distinct series', async () => {
  const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
  vi.mocked(getTokenUsageSummary).mockResolvedValue({
    inputTokens: 300, outputTokens: 0, cachedTokens: 0, totalTokens: 300, recordCount: 2, estimatedCount: 0,
    byModel: [breakdown('ClaudeCode', 100), breakdown('Cursor', 200)],
    buckets: [{
      bucketStartUtc: '2026-01-01T00:00:00Z', inputTokens: 300, outputTokens: 0, cachedTokens: 0, totalTokens: 300,
      models: [breakdown('ClaudeCode', 100), breakdown('Cursor', 200)],
    }],
  } as never);

  render(<TokenUsage />);

  // Each label appears in the legend and in the by-model chart.
  expect((await screen.findAllByText('shared-model (ClaudeCode)')).length).toBeGreaterThan(0);
  expect(screen.getAllByText('shared-model (Cursor)').length).toBeGreaterThan(0);
  expect(errorSpy.mock.calls.some((call) => String(call[0]).includes('same key'))).toBe(false);
});

test('offers auto-refresh', async () => {
  vi.mocked(getTokenUsageSummary).mockResolvedValue(null as never);
  render(<TokenUsage />);
  expect(await screen.findByTitle('Auto-refresh interval')).toBeInTheDocument();
});

test('reports input buckets and says when totals include legacy-rule records', async () => {
  vi.mocked(getTokenUsageSummary).mockResolvedValue({
    inputTokens: 500024, outputTokens: 6000, cachedTokens: 960000, totalTokens: 506024, recordCount: 2, estimatedCount: 0,
    uncachedInputTokens: 12, cacheReadInputTokens: 480000, cacheWriteInputTokens: 20000,
    legacyInputTokens: 12, legacyRecordCount: 1,
    byModel: [breakdown('ClaudeCode', 506024)],
    buckets: [],
  } as never);

  render(<TokenUsage />);

  expect(await screen.findByText('Uncached input')).toBeInTheDocument();
  expect(screen.getByText('Cache-read input')).toBeInTheDocument();
  expect(screen.getByText('Cache-write input')).toBeInTheDocument();
  expect(screen.getByText(/1 of 2 records predate the input buckets/)).toBeInTheDocument();
});
