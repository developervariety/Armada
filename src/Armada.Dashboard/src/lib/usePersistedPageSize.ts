import { useCallback, useState } from 'react';
import { PAGE_SIZE_OPTIONS, readPageSize, readStoredValue, rememberLastPageSize, writeStoredValue } from './pageSizePrefs';

/**
 * Page size for one table, persisted through the host's page-size store (localStorage in the dashboard) and reused on
 * every later visit. A table the user has not sized yet starts with the size they last chose anywhere, then
 * `initial`. Falls back to `initial` when storage is unavailable.
 */
export function usePersistedPageSize(key: string, initial = 25): [number, (size: number) => void] {
  const storageKey = `armada_pagesize_${key}`;
  const [size, setSize] = useState<number>(() => {
    const raw = readStoredValue(storageKey);
    return readPageSize(raw === null ? null : Number(raw), initial);
  });
  const update = useCallback((next: number) => {
    const value = PAGE_SIZE_OPTIONS.includes(next) ? next : initial;
    setSize(value);
    writeStoredValue(storageKey, String(value));
    rememberLastPageSize(value);
  }, [storageKey, initial]);
  return [size, update];
}
