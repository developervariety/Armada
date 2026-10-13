import { expect, it } from 'vitest';
import type { Persona } from '../types/models';
import { seedStepAssignments } from './dispatchRequest';

it('keeps personas with priorities on automatic routing despite a stored legacy default', () => {
  const persona = { name: 'Architect', defaultCaptainId: 'cpt_legacy', captainPriorities: [{ runtime: 'Mux' }] } as Persona;
  expect(seedStepAssignments({}, ['Architect'], [persona]).Architect.captainId).toBeNull();
  expect(seedStepAssignments({ Architect: { captainId: 'cpt_explicit', fallbackTier: null } }, ['Architect'], [persona]).Architect.captainId).toBe('cpt_explicit');
});

it('still prefills the legacy default for an empty hierarchy', () => {
  const persona = { name: 'Architect', defaultCaptainId: 'cpt_legacy', captainPriorities: [] } as unknown as Persona;
  expect(seedStepAssignments({}, ['Architect'], [persona]).Architect.captainId).toBe('cpt_legacy');
});
