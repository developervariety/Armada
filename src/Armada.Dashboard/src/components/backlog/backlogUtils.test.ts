import { describe, expect, it } from 'vitest';
import {
  buildObjectiveDispatchPrompt,
  buildObjectivePlanningPrompt,
  buildObjectiveReleaseNotes,
  getBacklogGroup,
  getLatestRefinementSessionId,
  getPriorityWeight,
  joinSuggestedPlaybooks,
  parseSuggestedPlaybooks,
  splitList,
} from './backlogUtils';
import type { Objective } from '../../types/models';

function createObjective(overrides: Partial<Objective> = {}): Objective {
  return {
    id: 'obj_123',
    tenantId: 'ten_123',
    userId: 'usr_123',
    title: 'Backlog hardening',
    description: 'Stabilize backlog replay and delivery flows.',
    status: 'Scoped',
    kind: 'Feature',
    category: 'Platform',
    priority: 'P1',
    rank: 12,
    backlogState: 'ReadyForPlanning',
    effort: 'M',
    owner: 'qa',
    targetVersion: '0.8.0',
    dueUtc: null,
    parentObjectiveId: null,
    blockedByObjectiveIds: [],
    refinementSummary: 'Use the selected captain transcript to sharpen acceptance criteria.',
    suggestedPipelineId: null,
    suggestedPlaybooks: [
      { playbookId: 'pb_inline', deliveryMode: 'InlineFullContent' },
      { playbookId: 'pb_reference', deliveryMode: 'InstructionWithReference' },
    ],
    refinementSessionIds: ['ref_old', 'ref_latest'],
    sourceProvider: null,
    sourceType: null,
    sourceId: null,
    sourceUrl: null,
    sourceUpdatedUtc: null,
    tags: ['backlog', 'refinement'],
    acceptanceCriteria: ['Replay fills parameters', 'History preserves redaction'],
    nonGoals: ['Redesign the API Explorer'],
    rolloutConstraints: ['Keep objective compatibility routes intact'],
    evidenceLinks: ['https://example.test/evidence'],
    fleetIds: [],
    vesselIds: [],
    planningSessionIds: [],
    voyageIds: [],
    missionIds: [],
    checkRunIds: [],
    releaseIds: [],
    deploymentIds: [],
    incidentIds: [],
    createdUtc: '2026-05-01T00:00:00Z',
    lastUpdateUtc: '2026-05-02T00:00:00Z',
    completedUtc: null,
    ...overrides,
  };
}

describe('backlogUtils', () => {
  it('splits and parses backlog list helpers', () => {
    expect(splitList('alpha, beta\n gamma \r\ndelta')).toEqual(['alpha', 'beta', 'gamma', 'delta']);
    expect(joinSuggestedPlaybooks([
      { playbookId: 'pb_inline', deliveryMode: 'InlineFullContent' },
      { playbookId: 'pb_reference', deliveryMode: 'InstructionWithReference' },
    ])).toBe('pb_inline:InlineFullContent\npb_reference:InstructionWithReference');
    expect(parseSuggestedPlaybooks('pb_inline:InlineFullContent\npb_default')).toEqual([
      { playbookId: 'pb_inline', deliveryMode: 'InlineFullContent' },
      { playbookId: 'pb_default', deliveryMode: 'InlineFullContent' },
    ]);
  });

  it('builds planning, dispatch, and release notes prompts from backlog metadata', () => {
    const objective = createObjective();

    const planningPrompt = buildObjectivePlanningPrompt(objective);
    const dispatchPrompt = buildObjectiveDispatchPrompt(objective);
    const releaseNotes = buildObjectiveReleaseNotes(objective);

    expect(planningPrompt).toContain('Backlog Item: Backlog hardening');
    expect(planningPrompt).toContain('Acceptance Criteria');
    expect(planningPrompt).toContain('Turn this refined backlog item into a practical implementation plan');

    expect(dispatchPrompt).toContain('Implement backlog item: Backlog hardening');
    expect(dispatchPrompt).toContain('Constraints');
    expect(dispatchPrompt).toContain('Keep objective compatibility routes intact');

    expect(releaseNotes).toContain('Backlog-derived release notes for Backlog hardening');
    expect(releaseNotes).toContain('Evidence Links');
    expect(releaseNotes).toContain('https://example.test/evidence');
  });

  it('derives backlog grouping, latest refinement session, and priority weights', () => {
    expect(getBacklogGroup(createObjective({ backlogState: 'Inbox' }))).toBe('inbox');
    expect(getBacklogGroup(createObjective({ backlogState: 'ReadyForDispatch' }))).toBe('dispatch');
    expect(getBacklogGroup(createObjective({ status: 'Blocked' }))).toBe('blocked');
    expect(getLatestRefinementSessionId(createObjective())).toBe('ref_latest');
    expect(getLatestRefinementSessionId(createObjective({ refinementSessionIds: [] }))).toBeNull();
    expect(getPriorityWeight('P0')).toBe(0);
    expect(getPriorityWeight('P3')).toBe(3);
  });
});
