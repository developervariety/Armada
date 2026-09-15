import { useCallback, useEffect, useRef, useState } from 'react';
import { getInbox } from '../api/client';
import type { InboxItem } from '../types/models';
import { useWebSocket } from '../context/WebSocketContext';

const POLL_INTERVAL_MS = 20000;
const WS_REFRESH_THROTTLE_MS = 4000;

export interface InboxCountState {
  items: InboxItem[];
  count: number;
  hasCritical: boolean;
  hasWarning: boolean;
}

/**
 * Live "Needs You" attention count. Polls the consolidated inbox (missions in review, failed
 * landings/missions, failed merges, deployments awaiting approval, stalled captains) on an interval and
 * refreshes promptly (throttled) when WebSocket activity arrives, so the sidebar badge tracks work that
 * needs a human without the user opening the Needs You page.
 *
 * The inbox route is administrator-only, so a caller that is not an administrator passes
 * `enabled = false` and no request is made. The throttle window starts when a request STARTS, not
 * when it succeeds, and only one request runs at a time: a refused or slow request must not turn
 * every WebSocket message into another request.
 */
export function useInboxCount(enabled: boolean = true): InboxCountState {
  const { subscribe } = useWebSocket();
  const [items, setItems] = useState<InboxItem[]>([]);
  const lastLoadRef = useRef(0);
  const inFlightRef = useRef(false);

  const load = useCallback(async () => {
    if (inFlightRef.current) return;
    inFlightRef.current = true;
    lastLoadRef.current = Date.now();
    try {
      const result = await getInbox();
      setItems(Array.isArray(result) ? result : []);
    } catch {
      // Best-effort: a failed poll leaves the last known count in place.
    } finally {
      inFlightRef.current = false;
    }
  }, []);

  useEffect(() => {
    if (!enabled) {
      setItems([]);
      return undefined;
    }
    void load();
    const timer = window.setInterval(() => { void load(); }, POLL_INTERVAL_MS);
    return () => window.clearInterval(timer);
  }, [enabled, load]);

  useEffect(() => {
    if (!enabled) return undefined;
    return subscribe(() => {
      if (Date.now() - lastLoadRef.current < WS_REFRESH_THROTTLE_MS) return;
      void load();
    });
  }, [enabled, subscribe, load]);

  return {
    items,
    count: items.length,
    hasCritical: items.some((item) => item.severity === 'Critical'),
    hasWarning: items.some((item) => item.severity === 'Warning'),
  };
}
