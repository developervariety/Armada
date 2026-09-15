import { describe, expect, it } from 'vitest';
import { buildFleetUpdatePayload } from './fleetPayload';
import type { Fleet } from '../types/models';

const fleet: Fleet = {
  id: 'flt_1',
  name: 'Fleet One',
  tenantId: null,
  description: 'old',
  defaultPipelineId: 'ppl_1',
  defaultPlaybooks: '[{"playbookId":"pbk_1","deliveryMode":"InlineFullContent"}]',
  active: false,
  createdUtc: '2026-01-01T00:00:00Z',
  lastUpdateUtc: '2026-01-01T00:00:00Z',
};

describe('buildFleetUpdatePayload', () => {
  it('sends back the fields the edit form does not own, so a full PUT does not clear them', () => {
    const payload = buildFleetUpdatePayload(fleet, { name: 'Renamed', description: 'new', defaultPipelineId: '' });
    expect(payload).toEqual({
      name: 'Renamed',
      description: 'new',
      defaultPipelineId: null,
      defaultPlaybooks: '[{"playbookId":"pbk_1","deliveryMode":"InlineFullContent"}]',
      active: false,
    });
  });
});
