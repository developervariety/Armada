/**
 * Persona key for a captain assignment that applies to every pipeline step. The server applies an exact persona
 * assignment first and uses this wildcard for any step without one.
 */
export const WILDCARD_PERSONA = '*';

/**
 * Personas that get a captain assignment row on dispatch. A selected pipeline offers one row per step; an inherited
 * pipeline has no known steps at dispatch time, so it offers one wildcard row that applies to every step.
 */
export function effectiveAssignmentPersonas(stepPersonas: string[]): string[] {
  return stepPersonas.length > 0 ? stepPersonas : [WILDCARD_PERSONA];
}
