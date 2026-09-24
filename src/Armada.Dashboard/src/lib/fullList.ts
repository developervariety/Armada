import type { EnumerationResult } from '../types/models';
import { listAllPages } from './listAllPages';

/** Page size a full-list read asks for; the server caps a page at this size. */
export const FULL_LIST_PAGE_SIZE = 1000;

type PageFetcher<T> = (params: { pageNumber: number; pageSize: number; filters?: Record<string, string> }) =>
  Promise<EnumerationResult<T>>;

/**
 * Read every row a server list holds under its server filters. A list page whose column filters or column sort run in
 * the browser reads the full set while one is active, so a filter or sort covers every row and not only the loaded page.
 */
export function loadEveryPage<T>(fetchPage: PageFetcher<T>, filters?: Record<string, string>): Promise<T[]> {
  return listAllPages((pageNumber) => fetchPage({ pageNumber, pageSize: FULL_LIST_PAGE_SIZE, filters }));
}

/** One page of rows the browser holds, with the requested page clamped to the last page. */
export function pageOfRows<T>(rows: T[], pageNumber: number, pageSize: number) {
  const totalPages = Math.max(1, Math.ceil(rows.length / pageSize));
  const currentPage = Math.min(Math.max(1, pageNumber), totalPages);
  const start = (currentPage - 1) * pageSize;
  return { rows: rows.slice(start, start + pageSize), currentPage, totalPages, totalRecords: rows.length };
}
