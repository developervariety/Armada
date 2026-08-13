import { useEffect, useRef, useState } from 'react';

/** Selectable auto-refresh intervals in seconds. 0 means "None" (no auto-refresh). */
export const AUTO_REFRESH_OPTIONS = [0, 15, 30, 60, 120, 180, 300] as const;
export const DEFAULT_AUTO_REFRESH_SECONDS = 15;

function storageKey(key: string): string {
  return `armada_autorefresh_${key}`;
}

function readStored(key: string): number {
  try {
    const raw = localStorage.getItem(storageKey(key));
    if (raw != null) {
      const parsed = Number(raw);
      if (AUTO_REFRESH_OPTIONS.includes(parsed as (typeof AUTO_REFRESH_OPTIONS)[number])) return parsed;
    }
  } catch {
    // Ignore storage access errors (private mode, disabled storage) and fall back to the default.
  }
  return DEFAULT_AUTO_REFRESH_SECONDS;
}

/**
 * Per-table auto-refresh timer. Persists the chosen interval per `key` in localStorage (default 15s) and
 * calls `onRefresh` on that cadence. Selecting "None" (0) disables the timer. The latest `onRefresh` is
 * always used, so callers can pass a fresh closure each render without resetting the interval.
 */
export function useAutoRefresh(key: string, onRefresh: () => void) {
  const [seconds, setSeconds] = useState<number>(() => readStored(key));
  const callbackRef = useRef(onRefresh);
  callbackRef.current = onRefresh;

  useEffect(() => {
    try {
      localStorage.setItem(storageKey(key), String(seconds));
    } catch {
      // Non-fatal: the interval still runs for this session even if persistence fails.
    }
    if (!seconds || seconds <= 0) return undefined;
    const id = window.setInterval(() => callbackRef.current(), seconds * 1000);
    return () => window.clearInterval(id);
  }, [seconds, key]);

  return { seconds, setSeconds };
}
