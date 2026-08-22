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
 */
export function useInboxCount(): InboxCountState {
  const { subscribe } = useWebSocket();
  const [items, setItems] = useState<InboxItem[]>([]);
  const lastLoadRef = useRef(0);

  const load = useCallback(async () => {
    try {
      const result = await getInbox();
      lastLoadRef.current = Date.now();
      setItems(Array.isArray(result) ? result : []);
    } catch {
      // Best-effort: a failed poll leaves the last known count in place.
    }
  }, []);

  useEffect(() => {
    void load();
    const timer = window.setInterval(() => { void load(); }, POLL_INTERVAL_MS);
    return () => window.clearInterval(timer);
  }, [load]);

  useEffect(() => {
    return subscribe(() => {
      if (Date.now() - lastLoadRef.current < WS_REFRESH_THROTTLE_MS) return;
      void load();
    });
  }, [subscribe, load]);

  return {
    items,
    count: items.length,
    hasCritical: items.some((item) => item.severity === 'Critical'),
    hasWarning: items.some((item) => item.severity === 'Warning'),
  };
}
