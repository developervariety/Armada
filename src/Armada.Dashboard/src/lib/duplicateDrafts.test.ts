import { describe, expect, it } from 'vitest';
import { buildMissionDuplicatePayload } from './duplicates';
import type { Mission } from '../types/models';

describe('mission duplication', () => {
  it('preserves reusable instructions and review policy while clearing all execution and dependency fields', () => {
    const source = { id: 'msn_old', title: 'Audit code', description: 'Full instructions', vesselId: 'vsl_1', mode: 'Audit', priority: 6,
      persona: 'Judge', requiresReview: true, reviewDenyAction: 'FailPipeline', selectedPlaybooks: [{ playbookId: 'pbk_1' }],
      status: 'Complete', captainId: 'cpt_old', voyageId: 'vyg_old', dependsOnMissionId: 'msn_dependency', branchName: 'old-branch', commitHash: 'abc', prUrl: 'old-url', processId: 123,
    } as unknown as Mission;
    const draft = buildMissionDuplicatePayload(source);
    expect(draft).toMatchObject({ title: 'Audit code (Copy)', description: 'Full instructions', mode: 'Audit', persona: 'Judge', requiresReview: true });
    for (const key of ['id', 'status', 'captainId', 'voyageId', 'dependsOnMissionId', 'branchName', 'commitHash', 'prUrl', 'processId']) expect(draft).not.toHaveProperty(key);
    expect(draft.selectedPlaybooks).not.toBe(source.selectedPlaybooks);
  });
});
