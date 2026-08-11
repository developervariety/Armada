import { describe, it, expect } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useResourceTable } from './useResourceTable';

interface Row {
  id: string;
  name: string;
  size: number;
}

const rows: Row[] = [
  { id: 'a', name: 'Charlie', size: 30 },
  { id: 'b', name: 'Alpha', size: 10 },
  { id: 'c', name: 'Bravo', size: 20 },
];

function setup(initialPageSize = 25) {
  return renderHook(() =>
    useResourceTable<Row>({
      rows,
      getId: (r) => r.id,
      columnValues: { name: (r) => r.name, size: (r) => r.size },
      searchFields: [(r) => r.name],
      initialPageSize,
    }),
  );
}

describe('useResourceTable', () => {
  it('filters by free-text search', () => {
    const { result } = setup();
    act(() => result.current.setSearch('alph'));
    expect(result.current.filtered.map((r) => r.id)).toEqual(['b']);
  });

  it('filters by a per-column filter', () => {
    const { result } = setup();
    act(() => result.current.setColFilter('name', 'bravo'));
    expect(result.current.filtered.map((r) => r.id)).toEqual(['c']);
  });

  it('sorts by a column and toggles direction', () => {
    const { result } = setup();
    act(() => result.current.handleSort('size'));
    expect(result.current.sorted.map((r) => r.size)).toEqual([10, 20, 30]);
    act(() => result.current.handleSort('size'));
    expect(result.current.sorted.map((r) => r.size)).toEqual([30, 20, 10]);
  });

  it('paginates and reports total pages', () => {
    const { result } = setup(2);
    expect(result.current.totalPages).toBe(2);
    expect(result.current.paginated).toHaveLength(2);
    act(() => result.current.setPageNumber(2));
    expect(result.current.paginated).toHaveLength(1);
  });

  it('tracks row selection', () => {
    const { result } = setup();
    act(() => result.current.toggleSelect('a'));
    expect(result.current.selected).toEqual(['a']);
    act(() => result.current.selectAll());
    expect(result.current.selected).toHaveLength(3);
    expect(result.current.allSelected).toBe(true);
    act(() => result.current.clearSelection());
    expect(result.current.selected).toEqual([]);
  });
});
