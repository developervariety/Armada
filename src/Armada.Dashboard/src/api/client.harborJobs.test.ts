import { afterEach, expect, test, vi } from 'vitest';
import { getHarborHostMetrics, getHarborJobs } from './client';

afterEach(() => vi.unstubAllGlobals());

test('encodes runner filters and bounds the Harbor jobs query limit', async () => {
  const fetchMock = vi.fn(async () => new Response(JSON.stringify({ Jobs: [] }), { status: 200 }));
  vi.stubGlobal('fetch', fetchMock);
  await expect(getHarborJobs({ runnerId: ' runner &? #', activeOnly: true, limit: 5000 })).resolves.toEqual({ jobs: [] });
  const [rawUrl, options] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
  const url = new URL(rawUrl, 'http://localhost');
  expect(url.pathname).toBe('/api/v1/harbor-runners/jobs');
  expect(url.searchParams.get('runnerId')).toBe('runner &? #');
  expect(url.searchParams.get('activeOnly')).toBe('true');
  expect(url.searchParams.get('limit')).toBe('1000');
  expect(options.method).toBe('GET');
});

test('requests host metrics for the selected runner', async () => {
  const fetchMock = vi.fn(async () => new Response(JSON.stringify({ state: 'no-data', points: [] }), { status: 200 }));
  vi.stubGlobal('fetch', fetchMock);
  await getHarborHostMetrics('host a/b');
  const [rawUrl] = fetchMock.mock.calls[0] as unknown as [string];
  expect(rawUrl).toContain('/api/v1/harbor-runners/host%20a%2Fb/metrics');
});
