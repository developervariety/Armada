import { useCallback, useEffect, useRef, useState } from 'react';
import { getVesselBranchCount, getVesselGitStatus } from '../api/client';

/** A vessel's commits ahead of and behind its remote default branch; null when unknown. */
export interface VesselSync {
  ahead: number | null;
  behind: number | null;
}

/**
 * How long a vessel's sync status and branch count are reused before the rows on screen ask again. Each status read
 * fetches from the remote and runs git on the vessel's host (a Harbor runs them as processes on the developer's
 * machine), and the server reuses it for two minutes as well.
 */
export const VESSEL_SYNC_TTL_MS = 2 * 60 * 1000;

/** Status and branch-count requests in flight at once. */
export const VESSEL_SYNC_CONCURRENCY = 4;

/** Run `work` over `items` with at most `limit` in flight. */
export async function eachLimited<T>(items: T[], limit: number, work: (item: T) => Promise<void>): Promise<void> {
  let next = 0;
  const runners = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (next < items.length) {
      const item = items[next];
      next += 1;
      await work(item);
    }
  });
  await Promise.all(runners);
}

/**
 * Sync status and branch counts for the vessels on screen (the Vessels list's Sync and Branches columns). Only the
 * given ids are read, a few at a time, and each is read again only after VESSEL_SYNC_TTL_MS; `reloadKey` (for
 * example the loaded vessel array) re-checks which are due after each list reload, `refresh` makes every vessel on
 * screen due now, and `refreshVessel` one vessel (after its branches changed).
 */
export function useVesselSyncStatus(visibleIds: string[], reloadKey: unknown) {
  const [gitStatus, setGitStatus] = useState<Record<string, VesselSync>>({});
  const [branchCounts, setBranchCounts] = useState<Record<string, number | null>>({});
  const [epoch, setEpoch] = useState(0);
  const fetchedAt = useRef<Record<string, number>>({});
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  const visibleKey = visibleIds.join('\n');
  useEffect(() => {
    const now = Date.now();
    const ids = visibleKey ? visibleKey.split('\n') : [];
    const due = ids.filter((id) => now - (fetchedAt.current[id] ?? 0) >= VESSEL_SYNC_TTL_MS);
    if (due.length === 0) return;
    // Claimed before the reads start, so a re-render while they run does not ask again.
    for (const id of due) fetchedAt.current[id] = now;
    void eachLimited(due, VESSEL_SYNC_CONCURRENCY, async (id) => {
      let sync: VesselSync = { ahead: null, behind: null };
      let count: number | null = null;
      try {
        const gs = await getVesselGitStatus(id);
        sync = { ahead: gs.commitsAhead, behind: gs.commitsBehind };
      } catch { /* unknown sync */ }
      try {
        count = await getVesselBranchCount(id);
      } catch { /* unknown count */ }
      if (!mounted.current) return;
      setGitStatus((prev) => ({ ...prev, [id]: sync }));
      setBranchCounts((prev) => ({ ...prev, [id]: count }));
    });
  }, [visibleKey, reloadKey, epoch]);

  const refresh = useCallback(() => {
    fetchedAt.current = {};
    setEpoch((e) => e + 1);
  }, []);

  const refreshVessel = useCallback((id: string) => {
    delete fetchedAt.current[id];
    setEpoch((e) => e + 1);
  }, []);

  return { gitStatus, branchCounts, refresh, refreshVessel };
}
