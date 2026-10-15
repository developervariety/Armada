import { describe, expect, it, vi } from 'vitest';
import { fetchAllPages, listAll, MAX_PAGE_SIZE } from './paging';
import type { EnumerationResult } from '../types/models';

interface Row { id: string }

function rows(from: number, count: number): Row[] {
  return Array.from({ length: count }, (_, i) => ({ id: `r_${from + i}` }));
}

/** A fake enumerate route over `total` rows that clamps the page size to `cap`, like the server does. */
function route(total: number, cap: number) {
  return vi.fn(async (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }): Promise<EnumerationResult<Row>> => {
    const pageSize = Math.min(Math.max(1, params?.pageSize ?? 10), cap);
    const pageNumber = Math.max(1, params?.pageNumber ?? 1);
    const start = (pageNumber - 1) * pageSize;
    const count = Math.max(0, Math.min(pageSize, total - start));
    return {
      success: true, pageNumber, pageSize, totalPages: Math.max(1, Math.ceil(total / pageSize)), totalRecords: total, totalMs: 1,
      objects: rows(start, count),
    };
  });
}

describe('listAll', () => {
  it('loads every page of a 1000-capped route (2500 records in three requests)', async () => {
    const list = route(2500, 1000);
    const result = await listAll(list);
    expect(result.objects).toHaveLength(2500);
    expect(result.totalRecords).toBe(2500);
    expect(list).toHaveBeenCalledTimes(3);
    expect(list).toHaveBeenNthCalledWith(1, { pageNumber: 1, pageSize: MAX_PAGE_SIZE });
    expect(list).toHaveBeenNthCalledWith(3, { pageNumber: 3, pageSize: MAX_PAGE_SIZE });
    expect(new Set(result.objects.map((r) => r.id)).size).toBe(2500);
  });

  it('follows the server clamp on a 500-capped route', async () => {
    const list = route(1200, 500);
    const result = await listAll(list);
    expect(result.objects).toHaveLength(1200);
    expect(list).toHaveBeenCalledTimes(3);
    expect(result.objects[1199].id).toBe('r_1199');
  });

  it('passes filters through on every page and ignores a caller pageSize', async () => {
    const list = route(3, 1000);
    await listAll(list, { filters: { fleetId: 'flt_1' } });
    expect(list).toHaveBeenCalledWith({ filters: { fleetId: 'flt_1' }, pageNumber: 1, pageSize: MAX_PAGE_SIZE });
  });

  it('makes one request when everything fits in the first page', async () => {
    const list = route(7, 1000);
    const result = await listAll(list);
    expect(list).toHaveBeenCalledTimes(1);
    expect(result.objects).toHaveLength(7);
    expect(result.totalRecords).toBe(7);
  });

  it('handles an empty route', async () => {
    const list = route(0, 1000);
    const result = await listAll(list);
    expect(list).toHaveBeenCalledTimes(1);
    expect(result.objects).toEqual([]);
    expect(result.totalRecords).toBe(0);
  });
});

describe('fetchAllPages', () => {
  it('stops at the safety limit instead of looping forever', async () => {
    let n = 0;
    const page = vi.fn(async (): Promise<EnumerationResult<Row>> => {
      n += 1;
      return { success: true, pageNumber: n, pageSize: 2, totalPages: 0, totalRecords: 1_000_000, totalMs: 0, objects: rows(n * 2, 2) };
    });
    const result = await fetchAllPages(page, { pageSize: 2, maxPages: 5 });
    expect(page).toHaveBeenCalledTimes(5);
    expect(result.objects).toHaveLength(10);
    expect(result.totalRecords).toBe(1_000_000);
  });

  it('stops when a page repeats rows it already returned', async () => {
    const page = vi.fn(async (): Promise<EnumerationResult<Row>> => (
      { success: true, pageNumber: 1, pageSize: 2, totalPages: 0, totalRecords: 50, totalMs: 0, objects: rows(0, 2) }
    ));
    const result = await fetchAllPages(page, { pageSize: 2 });
    expect(page).toHaveBeenCalledTimes(2);
    expect(result.objects).toHaveLength(2);
  });

  it('stops on a short page even when the route reports no total', async () => {
    const page = vi.fn(async (pageNumber: number): Promise<EnumerationResult<Row>> => (
      { objects: pageNumber === 1 ? rows(0, 3) : rows(3, 1), pageSize: 3 } as unknown as EnumerationResult<Row>
    ));
    const result = await fetchAllPages(page, { pageSize: 3 });
    expect(page).toHaveBeenCalledTimes(2);
    expect(result.objects).toHaveLength(4);
    expect(result.totalRecords).toBe(4);
  });

  it('propagates a failed page so callers can show their error state', async () => {
    const page = vi.fn(async (pageNumber: number): Promise<EnumerationResult<Row>> => {
      if (pageNumber === 2) throw new Error('boom');
      return { success: true, pageNumber, pageSize: 2, totalPages: 2, totalRecords: 4, totalMs: 0, objects: rows(0, 2) };
    });
    await expect(fetchAllPages(page, { pageSize: 2 })).rejects.toThrow('boom');
  });
});
