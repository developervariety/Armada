import { useRef } from 'react';

/** One load started by {@link useLatestRequest}. */
export interface LatestRequest {
  /** True until a load for this key has succeeded, so the page shows its spinner and reports a failure. */
  isInitialLoad: boolean;
  /** False once a later load has started; a superseded load must not write state. */
  isCurrent: () => boolean;
  /** Record that the record for this key is on screen, so later loads of the same key are background refreshes. */
  markLoaded: () => void;
}

/**
 * The one ordering rule for page loads: only the most recently started load may write state.
 *
 * Every load of a page (the first read, a filter, search, page or route-id change, an auto-refresh tick, a manual
 * refresh, a reload after a live-event gap) calls `begin(key)` before its request and checks `isCurrent()` after
 * each await. A load that a later load has superseded writes nothing, so a slower, older response never replaces a
 * newer one. `key` names what the load reads: a detail page passes its route id, a list page may pass its query or
 * nothing. A load is initial until a load for the same key succeeds, so navigating to another id shows the spinner
 * and surfaces a failure instead of leaving the previous record on screen, while a refresh of the record on screen
 * stays quiet.
 */
export function useLatestRequest() {
  const state = useRef({ generation: 0, loadedKey: undefined as string | undefined });
  const api = useRef({
    begin(key: string = ''): LatestRequest {
      const generation = ++state.current.generation;
      return {
        isInitialLoad: state.current.loadedKey !== key,
        isCurrent: () => state.current.generation === generation,
        markLoaded: () => {
          if (state.current.generation === generation) state.current.loadedKey = key;
        },
      };
    },
  });
  return api.current;
}
