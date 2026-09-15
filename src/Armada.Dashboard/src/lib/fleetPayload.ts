import type { Fleet } from '../types/models';

export interface FleetEditForm {
  name: string;
  description: string;
  defaultPipelineId: string;
}

/**
 * Body for `PUT /api/v1/fleets/{id}`. The update replaces the whole record, so the payload carries
 * the fields the edit form does not show (default playbooks, active flag) from the loaded fleet;
 * leaving them out would clear them.
 */
export function buildFleetUpdatePayload(fleet: Fleet, form: FleetEditForm): Partial<Fleet> {
  return {
    name: form.name,
    description: form.description,
    defaultPipelineId: form.defaultPipelineId || null,
    defaultPlaybooks: fleet.defaultPlaybooks ?? null,
    active: fleet.active,
  };
}
