/** Page sizes offered by the shared Pagination component's "n / page" selector. */
export const PAGE_SIZE_OPTIONS = [10, 25, 50, 100, 250];

const LAST_KEY = 'armada_pagesize__last';

/**
 * Where page-size choices are kept, and which screen a table without its own key belongs to. Host-agnostic: the
 * dashboard registers localStorage and the current route from main.tsx; other hosts (and tests) get the in-memory
 * default, which forgets choices when the app restarts.
 */
export interface PageSizeStore {
  get: (key: string) => string | null;
  set: (key: string, value: string) => void;
  /** Identifies the current screen; tables without a `pageSizeKey` remember their size per screen. */
  scope: () => string;
}

function memoryStore(): PageSizeStore {
  const values = new Map<string, string>();
  return {
    get: (key) => values.get(key) ?? null,
    set: (key, value) => { values.set(key, value); },
    scope: () => '',
  };
}

let store: PageSizeStore = memoryStore();

/** Register the host's storage for page-size choices. */
export function configurePageSizeStore(next: PageSizeStore): void {
  store = next;
}

/** The current screen's scope, for tables that remember their page size per screen. */
export function pageSizeScope(): string {
  try {
    return store.scope();
  } catch {
    return '';
  }
}

function valid(size: unknown): size is number {
  return typeof size === 'number' && PAGE_SIZE_OPTIONS.includes(size);
}

/** Read a stored value, or null when absent or storage fails. */
export function readStoredValue(key: string): string | null {
  try {
    return store.get(key);
  } catch {
    return null;
  }
}

/** Store a value; storage errors are ignored. */
export function writeStoredValue(key: string, value: string): void {
  try {
    store.set(key, value);
  } catch {
    // ignore storage errors
  }
}

/**
 * The page size to start a table with: the size last chosen on this table, else the size last chosen on any table
 * (so a choice carries over to tables not visited yet), else `fallback`. Storage errors fall back quietly.
 */
export function readPageSize(stored: unknown, fallback: number): number {
  if (valid(stored)) return stored;
  const last = Number(readStoredValue(LAST_KEY));
  if (valid(last)) return last;
  return fallback;
}

/** Remember a page size the user chose as the size for tables without their own choice. */
export function rememberLastPageSize(size: number): void {
  if (!valid(size)) return;
  writeStoredValue(LAST_KEY, String(size));
}
