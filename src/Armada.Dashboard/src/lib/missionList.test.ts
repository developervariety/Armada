import type { MissionSummary } from '../types/models';
import {
  MISSION_STATUSES,
  filterMissions,
  loadAllMissionSummaries,
  missionServerFilters,
  needsFullMissionList,
  sortMissions,
} from './missionList';

function summary(id: string, overrides: Partial<MissionSummary> = {}): MissionSummary {
  return {
    id,
    title: id,
    status: 'Pending',
    priority: 100,
    branchName: null,
    createdUtc: '2026-09-15T00:00:00Z',
    ...overrides,
  } as MissionSummary;
}

describe('missionList', () => {
  it('lists the landing and pull request statuses the server defines', () => {
    expect(MISSION_STATUSES).toContain('LandingFailed');
    expect(MISSION_STATUSES).toContain('PullRequestOpen');
  });

  it('pages on the server only for the creation-time order with no text filter', () => {
    const none = { title: '', status: '', branch: '' };
    expect(needsFullMissionList(none, 'createdUtc')).toBe(false);
    expect(needsFullMissionList(none, 'title')).toBe(true);
    expect(needsFullMissionList(none, 'priority')).toBe(true);
    expect(needsFullMissionList({ ...none, branch: 'fix/' }, 'createdUtc')).toBe(true);
  });

  it('sends the status filter and the creation-time order to the server', () => {
    expect(missionServerFilters('', 'desc')).toEqual({ order: 'CreatedDescending' });
    expect(missionServerFilters('LandingFailed', 'asc')).toEqual({ order: 'CreatedAscending', status: 'LandingFailed' });
  });

  it('reads every server page at the page cap', async () => {
    const pages = [[summary('a'), summary('b')], [summary('c')]];
    const fetchPage = vi.fn(async ({ pageNumber }: { pageNumber: number; pageSize: number; filters: Record<string, string> }) => ({
      success: true, pageNumber, pageSize: 1000, totalPages: 2, totalRecords: 3, totalMs: 1, objects: pages[pageNumber - 1],
    }));

    const rows = await loadAllMissionSummaries(fetchPage as never, { status: 'Failed' });

    expect(rows.map(r => r.id)).toEqual(['a', 'b', 'c']);
    expect(fetchPage).toHaveBeenCalledTimes(2);
    expect(fetchPage).toHaveBeenNthCalledWith(2, { pageNumber: 2, pageSize: 1000, filters: { status: 'Failed' } });
  });

  it('filters and sorts across all rows', () => {
    const rows = [
      summary('m1', { title: 'Port Eaton', priority: 5, branchName: 'armada/eaton' }),
      summary('m2', { title: 'Port Bendix', priority: 1, branchName: 'armada/bendix' }),
      summary('m3', { title: 'Fix docs', priority: 9, branchName: 'docs/fix' }),
    ];
    expect(filterMissions(rows, { title: 'port', status: '', branch: '' }).map(r => r.id)).toEqual(['m1', 'm2']);
    expect(filterMissions(rows, { title: '', status: '', branch: 'docs' }).map(r => r.id)).toEqual(['m3']);
    expect(sortMissions(rows, 'priority', 'asc').map(r => r.id)).toEqual(['m2', 'm1', 'm3']);
    expect(sortMissions(rows, 'title', 'desc').map(r => r.id)).toEqual(['m1', 'm2', 'm3']);
  });
});
