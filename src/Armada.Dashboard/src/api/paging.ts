import type { EnumerationResult } from '../types/models';

/**
 * Paging helpers for enumerate routes.
 *
 * Every enumerate route returns one page plus `totalRecords`, and every route clamps the page size (the shared
 * EnumerationQuery to 1000, many routes to 500). Asking for `pageSize: 9999` therefore silently returns the first
 * 1000 or 500 records. Use `listAll` wherever a page needs the complete set: pickers, id-to-name maps, and any total
 * or count computed on the client.
 *
 * Lives in its own module (not client.ts) so page tests that mock the client with a factory still get the real
 * helper, which then pages through the mocked list function it is given.
 */

/** Largest page size any enumerate route accepts; routes with a lower cap clamp it and report the size they used. */
export const MAX_PAGE_SIZE = 1000;

/** Safety stop so a misbehaving route can never loop forever (200 pages of 1000 is 200,000 records). */
export const MAX_PAGES = 200;

/** Parameters every paged list function accepts. */
export interface PageParams {
  pageNumber?: number;
  pageSize?: number;
}

/** Options for `listAll` / `fetchAllPages`. */
export interface ListAllOptions {
  /** Page size to request; the server clamps it to the route's maximum. Defaults to MAX_PAGE_SIZE. */
  pageSize?: number;
  /** Maximum number of pages to request before stopping. Defaults to MAX_PAGES. */
  maxPages?: number;
}

function recordKey(item: unknown): string | null {
  if (item && typeof item === 'object' && 'id' in item) {
    const id = (item as { id?: unknown }).id;
    if (typeof id === 'string' && id) return id;
  }
  return null;
}

/**
 * Request successive pages until the result holds `totalRecords` items, a page comes back short or empty, a page
 * adds nothing new, or the safety stop is reached. Returns one EnumerationResult whose `objects` is the complete set
 * and whose `totalRecords` is the server's total (or the collected count when the server did not report one).
 */
export async function fetchAllPages<T>(
  fetchPage: (pageNumber: number, pageSize: number) => Promise<EnumerationResult<T>>,
  options?: ListAllOptions,
): Promise<EnumerationResult<T>> {
  const requested = Math.max(1, options?.pageSize ?? MAX_PAGE_SIZE);
  const maxPages = Math.max(1, options?.maxPages ?? MAX_PAGES);
  const objects: T[] = [];
  const seen = new Set<string>();
  let first: EnumerationResult<T> | null = null;
  let totalRecords: number | null = null;
  let totalMs = 0;

  for (let pageNumber = 1; pageNumber <= maxPages; pageNumber++) {
    const page = await fetchPage(pageNumber, requested);
    if (!first) first = page;
    totalMs += typeof page?.totalMs === 'number' ? page.totalMs : 0;
    if (typeof page?.totalRecords === 'number' && page.totalRecords >= 0) totalRecords = page.totalRecords;

    const items = Array.isArray(page?.objects) ? page.objects : [];
    let added = 0;
    for (const item of items) {
      const key = recordKey(item);
      if (key !== null) {
        if (seen.has(key)) continue;
        seen.add(key);
      }
      objects.push(item);
      added++;
    }

    // The server reports the page size it actually used (after clamping); a page shorter than that is the last.
    const effectiveSize = typeof page?.pageSize === 'number' && page.pageSize > 0 ? Math.min(page.pageSize, requested) : requested;
    if (items.length === 0 || added === 0) break;
    if (totalRecords !== null && objects.length >= totalRecords) break;
    if (items.length < effectiveSize) break;
    if (typeof page?.totalPages === 'number' && page.totalPages > 0 && pageNumber >= page.totalPages) break;
  }

  return {
    success: first?.success ?? true,
    pageNumber: 1,
    pageSize: objects.length,
    totalPages: 1,
    totalRecords: Math.max(totalRecords ?? 0, objects.length),
    objects,
    totalMs,
  };
}

/**
 * Load every record from a paged list function, e.g. `listAll(listVessels)` or
 * `listAll(listDeployments, { releaseId })`. Any pageNumber / pageSize in `params` is ignored.
 */
export function listAll<T, P extends PageParams>(
  list: (params: P) => Promise<EnumerationResult<T>>,
  params?: Omit<P, 'pageNumber' | 'pageSize'>,
  options?: ListAllOptions,
): Promise<EnumerationResult<T>> {
  return fetchAllPages<T>(
    (pageNumber, pageSize) => list({ ...(params ?? {}), pageNumber, pageSize } as P),
    options,
  );
}
