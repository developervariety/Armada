import { useEffect, useRef } from 'react';
import { useSearchParams } from 'react-router-dom';

/** Open a duplication flow linked from a related-record table, once per requested ID. */
export function useDuplicateRequest(duplicate: (id: string) => Promise<void>, onError: (error: unknown) => void) {
  const [params, setParams] = useSearchParams();
  const id = params.get('duplicate');
  const handled = useRef<string | null>(null);
  useEffect(() => {
    if (!id) { handled.current = null; return; }
    if (handled.current === id) return;
    handled.current = id;
    const next = new URLSearchParams(params);
    next.delete('duplicate');
    setParams(next, { replace: true });
    void duplicate(id).catch(onError);
  }, [id, params, setParams, duplicate, onError]);
}
