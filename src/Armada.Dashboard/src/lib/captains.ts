import type { Captain } from '../types/models';

const planningReadyStates = new Set(['idle', 'available']);

export function isCaptainPlanningReadyState(state: string | null | undefined): boolean {
  return planningReadyStates.has((state ?? '').trim().toLowerCase());
}

export function canCaptainStartPlanning(captain: Pick<Captain, 'supportsPlanningSessions' | 'state'> | null | undefined): boolean {
  if (!captain?.supportsPlanningSessions) return false;
  return isCaptainPlanningReadyState(captain.state);
}
