import { useCallback, useEffect, useRef, useState } from 'react';
import { enumerateAskThreads } from '../api/client';
import type { AskThread } from '../types/models';
import { useWebSocket } from '../context/WebSocketContext';
import { parseAskEvent } from './askEvents';
import { INBOX_POLL_INTERVAL_MS } from './inboxSummary';

/** The enumerate route caps page size at 100. */
const ASK_UNREAD_PAGE_SIZE = 100;

/** Upper bound on pages read per refresh, so a huge history cannot turn one poll into a long crawl. */
const ASK_UNREAD_MAX_PAGES = 10;

/** Delay that coalesces a burst of Ask socket events into one refresh. */
export const ASK_UNREAD_SOCKET_DEBOUNCE_MS = 500;

/** Number of non-archived conversations with at least one unread message. */
export function countUnreadAskThreads(threads: readonly AskThread[] | null | undefined): number {
  if (!Array.isArray(threads)) return 0;
  return threads.filter((thread) => !!thread && !thread.archived && (thread.unreadCount ?? 0) > 0).length;
}

/** Read every non-archived Ask thread page and count the conversations with unread messages. */
export async function loadAskUnreadCount(): Promise<number> {
  const threads: AskThread[] = [];
  for (let page = 1; page <= ASK_UNREAD_MAX_PAGES; page++) {
    const result = await enumerateAskThreads({ pageNumber: page, pageSize: ASK_UNREAD_PAGE_SIZE, includeArchived: false });
    const objects = Array.isArray(result?.objects) ? result.objects : [];
    threads.push(...objects);
    if (objects.length === 0 || page >= (result?.totalPages ?? 1)) break;
  }
  return countUnreadAskThreads(threads);
}

/**
 * Live count of Ask Armada conversations with unread messages, for the sidebar badge. Polls the thread list on
 * the same interval as the Needs You badge and refreshes shortly after Ask thread, message or turn events arrive.
 * A failed load reports 0.
 */
export function useAskUnreadCount(): number {
  const { subscribe } = useWebSocket();
  const [count, setCount] = useState(0);
  const debounceRef = useRef<number | null>(null);

  const load = useCallback(async () => {
    try {
      setCount(await loadAskUnreadCount());
    } catch {
      setCount(0);
    }
  }, []);

  useEffect(() => {
    void load();
    const timer = window.setInterval(() => { void load(); }, INBOX_POLL_INTERVAL_MS);
    return () => window.clearInterval(timer);
  }, [load]);

  useEffect(() => {
    const unsubscribe = subscribe((msg) => {
      const event = parseAskEvent(msg);
      if (!event || (event.type !== 'ask.thread' && event.type !== 'ask.message' && event.type !== 'ask.turn')) return;
      if (debounceRef.current !== null) return;
      debounceRef.current = window.setTimeout(() => {
        debounceRef.current = null;
        void load();
      }, ASK_UNREAD_SOCKET_DEBOUNCE_MS);
    });
    return () => {
      unsubscribe();
      if (debounceRef.current !== null) {
        window.clearTimeout(debounceRef.current);
        debounceRef.current = null;
      }
    };
  }, [subscribe, load]);

  return count;
}
