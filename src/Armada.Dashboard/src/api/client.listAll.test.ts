import { afterEach, expect, test, vi } from 'vitest';
import { listAllFleets, listAllVessels } from './client';

afterEach(() => {
  vi.unstubAllGlobals();
});

/** Serves an enumeration the way the server pages it: default page size 10, capped at 1000. */
function stubEnumeration(total: number) {
  const requests: URL[] = [];
  vi.stubGlobal('fetch', vi.fn(async (input: string) => {
    const url = new URL(input, 'http://localhost');
    requests.push(url);
    const pageSize = Math.min(Number(url.searchParams.get('pageSize') || 10), 1000);
    const pageNumber = Number(url.searchParams.get('pageNumber') || 1);
    const start = (pageNumber - 1) * pageSize;
    const objects = Array.from({ length: Math.max(0, Math.min(pageSize, total - start)) }, (_, index) => ({ id: `rec_${start + index}`, name: `Record ${start + index}` }));
    const body = { success: true, pageNumber, pageSize, totalPages: Math.ceil(total / pageSize), totalRecords: total, totalMs: 1, objects };
    return new Response(JSON.stringify(body), { status: 200 });
  }));
  return requests;
}

test.each([
  ['fleets', listAllFleets, '/api/v1/fleets'],
  ['vessels', listAllVessels, '/api/v1/vessels'],
] as const)('reads every page of %s instead of the server default page', async (_name, listAll, path) => {
  const requests = stubEnumeration(1005);

  const all = await listAll();

  expect(all).toHaveLength(1005);
  expect(new Set(all.map((record) => record.id)).size).toBe(1005);
  expect(requests.map((url) => url.pathname)).toEqual([path, path]);
  expect(requests.map((url) => url.searchParams.get('pageNumber'))).toEqual(['1', '2']);
  expect(requests.every((url) => url.searchParams.get('pageSize') === '1000')).toBe(true);
});
