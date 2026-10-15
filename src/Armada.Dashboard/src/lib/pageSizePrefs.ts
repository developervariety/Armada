/** Page sizes offered by the shared Pagination component's "n / page" selector. */
export const PAGE_SIZE_OPTIONS = [10, 25, 50, 100, 250];

const LAST_KEY = 'armada_pagesize__last';

function valid(size: unknown): size is number {
  return typeof size === 'number' && PAGE_SIZE_OPTIONS.includes(size);
}

/**
 * The page size to start a table with: the size last chosen on this table, else the size last chosen on any table
 * (so a choice carries over to tables not visited yet), else `fallback`. Storage errors fall back quietly.
 */
export function readPageSize(stored: unknown, fallback: number): number {
  if (valid(stored)) return stored;
  try {
    const last = Number(localStorage.getItem(LAST_KEY));
    if (valid(last)) return last;
  } catch {
    // ignore storage errors
  }
  return fallback;
}

/** Remember a page size the user chose as the size for tables without their own choice. */
export function rememberLastPageSize(size: number): void {
  if (!valid(size)) return;
  try {
    localStorage.setItem(LAST_KEY, String(size));
  } catch {
    // ignore storage errors
  }
}
