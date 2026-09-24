import { useEffect, useState } from 'react';

/** How long a search box waits after the last keystroke before its page queries the server. */
export const SEARCH_DEBOUNCE_MS = 300;

/**
 * Returns `value` once it has stopped changing for `delayMs`.
 *
 * A search box keeps its own state for what the user sees and passes it through this hook; the page loads from the
 * returned value, so typing a word sends one request after the user pauses instead of one per keystroke.
 */
export function useDebouncedValue<T>(value: T, delayMs: number = SEARCH_DEBOUNCE_MS): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);
  return debounced;
}
