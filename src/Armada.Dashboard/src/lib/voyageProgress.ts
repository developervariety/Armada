export interface VoyageProgressSummary {
  total: number;
  completed: number;
  /** Missions that ended in Failed or LandingFailed. */
  failed: number;
  cancelled: number;
  /** Missions that will not change again: completed, failed or cancelled. */
  finished: number;
  /** Finished missions as a whole-number percentage of all missions. */
  percent: number;
}

const FAILED_STATUSES = new Set(['Failed', 'LandingFailed']);

/**
 * Progress of a voyage from its missions. A landing failure is a failure and a cancelled mission is
 * finished; counting only Complete and Failed left voyages with those missions stuck short of done.
 */
export function summarizeVoyageProgress(missions: Array<{ status: string }>): VoyageProgressSummary {
  let completed = 0;
  let failed = 0;
  let cancelled = 0;
  for (const mission of missions) {
    if (mission.status === 'Complete') completed++;
    else if (FAILED_STATUSES.has(mission.status)) failed++;
    else if (mission.status === 'Cancelled') cancelled++;
  }
  const total = missions.length;
  const finished = completed + failed + cancelled;
  return { total, completed, failed, cancelled, finished, percent: total > 0 ? Math.round((finished / total) * 100) : 0 };
}
