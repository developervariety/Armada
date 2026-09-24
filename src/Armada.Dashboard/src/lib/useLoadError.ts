import { useCallback, useRef, useState } from 'react';

/**
 * The error state of a page whose list reloads on a timer. A failed load opens the error dialog once; while loads keep
 * failing the dialog stays closed after the user dismisses it, and the next failure after a successful load opens it
 * again. Errors from user actions use
 * `setError` and always show.
 */
export function useLoadError() {
  const [error, setError] = useState('');
  const failing = useRef(false);

  const loadFailed = useCallback((message: string) => {
    if (failing.current) return;
    failing.current = true;
    setError(message);
  }, []);

  const loadSucceeded = useCallback(() => {
    failing.current = false;
    setError('');
  }, []);

  return { error, setError, loadFailed, loadSucceeded };
}
