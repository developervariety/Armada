import { describe, expect, it, vi } from 'vitest';
import { listAllPages } from './listAllPages';
import type { EnumerationResult } from '../types/models';

function page(pageNumber: number, totalPages: number, ids: string[]): EnumerationResult<{ id: string }> {
  return {
    success: true,
    pageNumber,
    pageSize: 2,
    totalPages,
    totalRecords: 5,
    totalMs: 1,
    objects: ids.map((id) => ({ id })),
  };
}

describe('listAllPages', () => {
  it('reads every page until the server reports the last one', async () => {
    const fetchPage = vi.fn(async (pageNumber: number) => {
      if (pageNumber === 1) return page(1, 3, ['a', 'b']);
      if (pageNumber === 2) return page(2, 3, ['c', 'd']);
      return page(3, 3, ['e']);
    });

    const all = await listAllPages(fetchPage);

    expect(all.map((item) => item.id)).toEqual(['a', 'b', 'c', 'd', 'e']);
    expect(fetchPage).toHaveBeenCalledTimes(3);
  });

  it('stops on an empty page even when the total says more pages exist', async () => {
    const fetchPage = vi.fn(async (pageNumber: number) => (pageNumber === 1 ? page(1, 9, ['a']) : page(pageNumber, 9, [])));
    const all = await listAllPages(fetchPage);
    expect(all.map((item) => item.id)).toEqual(['a']);
    expect(fetchPage).toHaveBeenCalledTimes(2);
  });
});
