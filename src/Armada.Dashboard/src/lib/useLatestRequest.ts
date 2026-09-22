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
 * Orders the loads of a detail page whose record key (the route id) can change while a request is in flight.
 * Every load calls `begin(key)`; only the most recently started load is current, so an earlier response can
 * never replace a later one. A load is initial until a load for the same key succeeds, so navigating to another
 * id shows the spinner and surfaces a failure instead of leaving the previous record on screen.
 */
export function useLatestRequest() {
  const state = useRef({ generation: 0, loadedKey: undefined as string | undefined });
  const api = useRef({
    begin(key: string): LatestRequest {
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
