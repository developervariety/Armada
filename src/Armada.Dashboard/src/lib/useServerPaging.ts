import { useCallback, useRef, useState } from 'react';

/** The page totals a server enumeration returns. */
export interface ServerPageTotals {
  totalPages?: number | null;
  totalRecords?: number | null;
}

export interface ServerPagingOptions {
  initialPageSize?: number;
  /** Largest page size the endpoint accepts; a larger choice is capped to it. */
  maxPageSize?: number;
}

/**
 * Paging state for a list whose pages the server returns: the requested page and size, the totals of the last
 * page read, and the rules every such list needs. A page-size change or a filter change (`resetPage`) returns to
 * page 1. `acceptPage` records the totals of a loaded page and refuses a page past the end: when rows were deleted
 * or expired and the list now ends before the requested page, it moves to the last page and returns false, so the
 * caller keeps the rows it has instead of showing an empty table, and the page change reloads the last page.
 */
export function useServerPaging(options: ServerPagingOptions = {}) {
  const { initialPageSize = 25, maxPageSize } = options;
  const [pageNumber, setPageNumberState] = useState(1);
  const [pageSize, setPageSizeState] = useState(initialPageSize);
  const [totalPages, setTotalPages] = useState(1);
  const [totalRecords, setTotalRecords] = useState(0);
  const requestedPage = useRef(1);
  requestedPage.current = pageNumber;

  const setPageNumber = useCallback((page: number) => {
    setPageNumberState(Math.max(1, Math.floor(page) || 1));
  }, []);

  const resetPage = useCallback(() => setPageNumberState(1), []);

  const setPageSize = useCallback((size: number) => {
    setPageSizeState(maxPageSize ? Math.min(size, maxPageSize) : size);
    setPageNumberState(1);
  }, [maxPageSize]);

  const acceptPage = useCallback((result: ServerPageTotals): boolean => {
    const lastPage = Math.max(1, result.totalPages || 0);
    setTotalPages(lastPage);
    setTotalRecords(result.totalRecords || 0);
    if (requestedPage.current > lastPage) {
      setPageNumberState(lastPage);
      return false;
    }
    return true;
  }, []);

  return { pageNumber, pageSize, totalPages, totalRecords, setPageNumber, setPageSize, resetPage, acceptPage };
}
