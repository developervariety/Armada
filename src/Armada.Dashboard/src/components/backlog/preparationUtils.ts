import type { ObjectivePreparation } from '../../types/models';

export function parseObjectivePreparationJson(raw: string): ObjectivePreparation {
  const parsed: unknown = JSON.parse(raw);
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('Dispatch preparation must be a JSON object.');
  }

  const value = parsed as Partial<ObjectivePreparation>;
  if (typeof value.requiredForDispatch !== 'boolean'
    || !Array.isArray(value.requiredClaimKinds)
    || !Array.isArray(value.requiredSiblingInputs)
    || !Array.isArray(value.claims)) {
    throw new Error('Dispatch preparation must include requiredForDispatch, requiredClaimKinds, requiredSiblingInputs, and claims.');
  }

  return value as ObjectivePreparation;
}
