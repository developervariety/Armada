import { useCallback, useState } from 'react';
import { PAGE_SIZE_OPTIONS, readPageSize, rememberLastPageSize } from './pageSizePrefs';

/**
 * Page size for one table, persisted in localStorage and reused on every later visit. A table the user has not
 * sized yet starts with the size they last chose anywhere, then `initial`. Falls back to `initial` when storage is
 * unavailable.
 */
export function usePersistedPageSize(key: string, initial = 25): [number, (size: number) => void] {
  const storageKey = `armada_pagesize_${key}`;
  const [size, setSize] = useState<number>(() => {
    let stored: number | null = null;
    try {
      const raw = localStorage.getItem(storageKey);
      stored = raw === null ? null : Number(raw);
    } catch {
      // ignore storage errors
    }
    return readPageSize(stored, initial);
  });
  const update = useCallback((next: number) => {
    const value = PAGE_SIZE_OPTIONS.includes(next) ? next : initial;
    setSize(value);
    try {
      localStorage.setItem(storageKey, String(value));
    } catch {
      // ignore storage errors
    }
    rememberLastPageSize(value);
  }, [storageKey, initial]);
  return [size, update];
}
