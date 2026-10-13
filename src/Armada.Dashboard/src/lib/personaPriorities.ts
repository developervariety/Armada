import type { Persona, PersonaCaptainPriority, PersonaPriorityAvailability, PersonaPriorityExhaustion } from '../types/models';
import { CAPTAIN_RUNTIMES, CAPTAIN_TIERS } from './captainForm';

export const PERSONA_PRIORITY_RUNTIMES = [...CAPTAIN_RUNTIMES, { value: 'Custom', label: 'Custom' }];

export interface PersonaRoutingForm {
  captainPriorities: PersonaCaptainPriority[];
  priorityAvailability: PersonaPriorityAvailability;
  priorityExhaustion: PersonaPriorityExhaustion;
}

export function personaRoutingForm(persona?: Partial<Persona>): PersonaRoutingForm {
  return {
    captainPriorities: (persona?.captainPriorities ?? []).map(p => ({ ...p })),
    priorityAvailability: persona?.priorityAvailability ?? 'FallThrough',
    priorityExhaustion: persona?.priorityExhaustion ?? 'Wait',
  };
}

/** Validate before submission; returns a localization key, or null. */
export function validatePersonaPriorities(priorities: PersonaCaptainPriority[]): string | null {
  for (const p of priorities) {
    if (!p || (!p.runtime && !p.model?.trim() && !p.endpoint?.trim() && !p.minimumTier)) return 'Each priority needs at least one matching field.';
    if (p.runtime && !PERSONA_PRIORITY_RUNTIMES.some(r => r.value === p.runtime)) return 'Invalid captain runtime.';
    if (p.minimumTier && !CAPTAIN_TIERS.includes(p.minimumTier)) return 'Invalid captain tier.';
    if (p.endpoint && p.runtime !== 'Mux') return 'An endpoint requires the Mux runtime.';
    if ((p.model != null && !p.model.trim()) || (p.endpoint != null && !p.endpoint.trim())) return 'Matching fields must not be blank.';
  }
  return null;
}

export function prioritySummary(priority: PersonaCaptainPriority): string {
  return [priority.runtime, priority.model, priority.endpoint, priority.minimumTier].filter(Boolean).join(' / ');
}
