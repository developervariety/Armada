import { useEffect, useMemo, useState } from 'react';
import { getVesselGitStatus } from '../api/client';

/** Most status reads in flight at once, so a page of vessels does not start a page of git processes together. */
export const VESSEL_GIT_STATUS_CONCURRENCY = 4;

export interface VesselSyncStatus {
  ahead: number | null;
  behind: number | null;
}

/**
 * Read the git sync status of the vessels on screen, a few at a time. Only the given ids are read, so a list of
 * many vessels reads one page of them, and a new `refreshKey` reads those ids again (the server reuses a recent
 * status). A failed read leaves that vessel without a status.
 */
export function useVesselGitStatus(vesselIds: string[], refreshKey: number): Record<string, VesselSyncStatus> {
  const [statuses, setStatuses] = useState<Record<string, VesselSyncStatus>>({});
  const idKey = useMemo(() => vesselIds.join(','), [vesselIds]);

  useEffect(() => {
    const ids = idKey ? idKey.split(',') : [];
    let cancelled = false;
    let next = 0;

    async function worker() {
      while (!cancelled && next < ids.length) {
        const id = ids[next++];
        let status: VesselSyncStatus;
        try {
          const result = await getVesselGitStatus(id);
          status = { ahead: result.commitsAhead, behind: result.commitsBehind };
        } catch {
          status = { ahead: null, behind: null };
        }
        if (cancelled) return;
        setStatuses((current) => ({ ...current, [id]: status }));
      }
    }

    void Promise.all(Array.from({ length: Math.min(VESSEL_GIT_STATUS_CONCURRENCY, ids.length) }, worker));
    return () => { cancelled = true; };
  }, [idKey, refreshKey]);

  return statuses;
}
