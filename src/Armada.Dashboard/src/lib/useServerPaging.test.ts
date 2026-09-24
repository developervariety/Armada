import { describe, it, expect } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useServerPaging } from './useServerPaging';

describe('useServerPaging', () => {
  it('moves to the last page when a load returns a page past the end', () => {
    const { result } = renderHook(() => useServerPaging());
    act(() => { result.current.acceptPage({ totalPages: 3, totalRecords: 75 }); });
    act(() => result.current.setPageNumber(3));

    let accepted = true;
    act(() => { accepted = result.current.acceptPage({ totalPages: 2, totalRecords: 50 }); });

    expect(accepted).toBe(false);
    expect(result.current.pageNumber).toBe(2);
    expect(result.current.totalPages).toBe(2);
  });

  it('accepts a page inside the list and treats an empty list as one page', () => {
    const { result } = renderHook(() => useServerPaging());
    let accepted = false;
    act(() => { accepted = result.current.acceptPage({ totalPages: 0, totalRecords: 0 }); });
    expect(accepted).toBe(true);
    expect(result.current.totalPages).toBe(1);
    expect(result.current.pageNumber).toBe(1);
  });

  it('returns to page 1 on a page-size change and caps the size', () => {
    const { result } = renderHook(() => useServerPaging({ maxPageSize: 500 }));
    act(() => result.current.setPageNumber(4));
    act(() => result.current.setPageSize(1000));
    expect(result.current.pageNumber).toBe(1);
    expect(result.current.pageSize).toBe(500);
  });
});
