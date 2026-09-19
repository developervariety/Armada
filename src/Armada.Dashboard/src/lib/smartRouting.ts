import type { Captain } from '../types/models';
import { policyAccounts, type PolicyRecord } from './subscriptionAccounts';

export type ModelListName = 'default' | 'lighter' | 'stronger';
export const MODEL_LISTS: ModelListName[] = ['default', 'lighter', 'stronger'];

export interface PersonaModelEntry { default: string[]; lighter: string[]; stronger: string[] }
export interface PersonaRoute { accountId: string; models: string[] }

/** How many captains run a model, and how many of them sit on an account that can take work. */
export interface ModelAvailability {
  model: string;
  captains: number;
  exhausted: number;
  /** True when the model has captains and every one of them is on an Exhausted account. */
  allExhausted: boolean;
}

/** One persona model-list entry the server marked as having no eligible captain. */
export interface DeadPersonaModelEntry {
  persona: string;
  list: string;
  model: string;
}

/** True when the server marked this persona/list/model as dead. */
export function isDeadModel(
  dead: DeadPersonaModelEntry[] | undefined, persona: string, list: ModelListName, model: string,
): boolean {
  if (!dead || dead.length === 0) return false;
  const wantedPersona = normalizePersona(persona);
  return dead.some((entry) =>
    normalizePersona(entry.persona) === wantedPersona
    && entry.list.toLowerCase() === list
    && entry.model.toLowerCase() === model.toLowerCase());
}

/** Persona names match after normalization, the same way the server compares them (case, spaces, punctuation). */
export function normalizePersona(name: string): string {
  return name.toLowerCase().replace(/[^a-z0-9*]/g, '');
}

function stringList(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : [];
}

/** The `personaModels` map of a policy, with every list present. */
export function personaModels(policy: PolicyRecord | null): Record<string, PersonaModelEntry> {
  const raw = policy?.personaModels;
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
  const result: Record<string, PersonaModelEntry> = {};
  for (const [persona, entry] of Object.entries(raw as Record<string, unknown>)) {
    const e = (entry && typeof entry === 'object' ? entry : {}) as Record<string, unknown>;
    result[persona] = { default: stringList(e.default), lighter: stringList(e.lighter), stronger: stringList(e.stronger) };
  }
  return result;
}

/** The `personaRoutes` map of a policy. */
export function personaRoutes(policy: PolicyRecord | null): Record<string, PersonaRoute[]> {
  const raw = policy?.personaRoutes;
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
  const result: Record<string, PersonaRoute[]> = {};
  for (const [persona, routes] of Object.entries(raw as Record<string, unknown>)) {
    result[persona] = (Array.isArray(routes) ? routes : []).map((r) => {
      const route = (r && typeof r === 'object' ? r : {}) as Record<string, unknown>;
      return { accountId: typeof route.accountId === 'string' ? route.accountId : '', models: stringList(route.models) };
    });
  }
  return result;
}

/** The existing key that names this persona after normalization, or null. */
export function findPersonaKey(keys: string[], persona: string): string | null {
  const wanted = normalizePersona(persona);
  return keys.find((k) => normalizePersona(k) === wanted) ?? null;
}

/**
 * Rows for the persona model table: every persona from the catalogue, then every persona already in the policy
 * that the catalogue does not name, then extra rows the operator added. A catalogue persona reuses its policy key.
 */
export function personaRows(catalogue: string[], entries: string[], extras: string[]): string[] {
  const rows: string[] = [];
  const seen = new Set<string>();
  const add = (name: string) => {
    const norm = normalizePersona(name);
    if (!name.trim() || name === '*' || seen.has(norm)) return;
    seen.add(norm);
    rows.push(name);
  };
  for (const name of catalogue) add(findPersonaKey(entries, name) ?? name);
  for (const name of entries) add(name);
  for (const name of extras) add(name);
  return rows;
}

/** Model options: every captain's model, without duplicates, in first-seen order. */
export function modelOptions(captains: Captain[]): string[] {
  const out: string[] = [];
  for (const model of captains.map((c) => c.model ?? '')) {
    const m = model.trim();
    if (m && !out.includes(m)) out.push(m);
  }
  return out;
}

/** The account id that lists a captain, or null when the captain is on no account. */
export function captainAccount(policy: PolicyRecord | null, captainId: string): string | null {
  const account = policyAccounts(policy).find((a) => stringList(a.captainIds).includes(captainId));
  return account ? String(account.id) : null;
}

/** Availability per model from captains, the account each captain is on, and the reported account states. */
export function modelAvailability(
  models: string[], captains: Captain[], policy: PolicyRecord | null, statuses: Array<Record<string, unknown>>,
): Record<string, ModelAvailability> {
  const state = new Map(statuses.map((s) => [String(s.accountId), String(s.state)]));
  const result: Record<string, ModelAvailability> = {};
  for (const model of models) {
    const running = captains.filter((c) => (c.model ?? '').trim() === model);
    const exhausted = running.filter((c) => {
      const account = captainAccount(policy, c.id);
      return account !== null && state.get(account) === 'Exhausted';
    }).length;
    result[model] = { model, captains: running.length, exhausted, allExhausted: running.length > 0 && exhausted === running.length };
  }
  return result;
}

/** Captains a persona restriction still admits: on a named account, and running a listed model when the route lists any. */
export function captainsAdmitted(routes: PersonaRoute[], captains: Captain[], policy: PolicyRecord | null): Captain[] {
  return captains.filter((c) => {
    const account = captainAccount(policy, c.id);
    return routes.some((r) => r.accountId === account && (r.models.length === 0 || r.models.includes((c.model ?? '').trim())));
  });
}
