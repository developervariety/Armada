import type { EnumerationResult } from '../types/models';

/** Upper bound on pages read, so a server that misreports its totals cannot loop the client. */
const MAX_PAGES = 1000;

/**
 * Read every page of a server enumeration. The server caps a page (500 for objectives), so asking
 * for one large page silently returns only the first part of the set. Reading stops when the
 * server reports the last page or returns an empty page.
 */
export async function listAllPages<T>(fetchPage: (pageNumber: number) => Promise<EnumerationResult<T>>): Promise<T[]> {
  const all: T[] = [];
  for (let pageNumber = 1; pageNumber <= MAX_PAGES; pageNumber++) {
    const result = await fetchPage(pageNumber);
    const objects = result.objects || [];
    all.push(...objects);
    if (objects.length === 0 || pageNumber >= (result.totalPages || 1)) break;
  }
  return all;
}
