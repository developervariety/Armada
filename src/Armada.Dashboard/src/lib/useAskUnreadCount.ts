import { useCallback, useEffect, useState } from 'react';
import { listAskThreads } from '../api/client';

const POLL_INTERVAL_MS = 30000;

/** Dispatched on window when the Ask page changes a conversation's unread state, so the sidebar badge updates at once. */
export const ASK_UNREAD_CHANGED_EVENT = 'armada:ask-unread-changed';

/** Tells the sidebar badge to recount unread Ask conversations now. */
export function notifyAskUnreadChanged(): void {
  window.dispatchEvent(new Event(ASK_UNREAD_CHANGED_EVENT));
}

/**
 * Number of saved Ask conversations that hold a reply the signed-in user has not opened. The server keeps the
 * unread state; this hook counts it on an interval and when the Ask page reports a change. The Ask routes are
 * administrator-only, so a caller that is not an administrator passes `enabled = false` and no request is made.
 */
export function useAskUnreadCount(enabled: boolean = true): number {
  const [count, setCount] = useState(0);

  const load = useCallback(async () => {
    try {
      const threads = await listAskThreads(false);
      setCount(threads.filter((thread) => (thread.unreadCount ?? 0) > 0).length);
    } catch {
      // Best-effort: a failed poll leaves the last known count in place.
    }
  }, []);

  useEffect(() => {
    if (!enabled) { setCount(0); return undefined; }
    void load();
    const timer = window.setInterval(() => { if (!document.hidden) void load(); }, POLL_INTERVAL_MS);
    window.addEventListener(ASK_UNREAD_CHANGED_EVENT, load);
    return () => { window.clearInterval(timer); window.removeEventListener(ASK_UNREAD_CHANGED_EVENT, load); };
  }, [enabled, load]);

  return count;
}
