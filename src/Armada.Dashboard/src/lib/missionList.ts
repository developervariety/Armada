import type { EnumerationResult, MissionSummary } from '../types/models';

/** Mission statuses the server defines, in lifecycle order. */
export const MISSION_STATUSES = [
  'Pending', 'Assigned', 'InProgress', 'WorkProduced', 'PullRequestOpen', 'Testing', 'Review', 'Complete', 'Failed',
  'LandingFailed', 'Cancelled',
];

/** Largest page the mission summaries endpoint returns. */
export const MISSION_SUMMARY_PAGE_CAP = 1000;

export type MissionSortField = 'title' | 'status' | 'priority' | 'createdUtc';
export type MissionSortDir = 'asc' | 'desc';

export interface MissionColumnFilters {
  title: string;
  status: string;
  branch: string;
}

type MissionPageFetcher = (params: { pageNumber: number; pageSize: number; filters: Record<string, string> }) =>
  Promise<EnumerationResult<MissionSummary>>;

/**
 * The summaries endpoint filters by status, vessel, captain and voyage and orders only by creation time. A title,
 * status or branch text filter, or a sort on any other column, must therefore see every mission rather than the
 * current server page.
 */
export function needsFullMissionList(filters: MissionColumnFilters, sortField: MissionSortField): boolean {
  return Boolean(filters.title || filters.status || filters.branch) || sortField !== 'createdUtc';
}

/** Server query filters for a list request: the status filter plus the creation-time order. */
export function missionServerFilters(statusFilter: string, sortDir: MissionSortDir): Record<string, string> {
  const filters: Record<string, string> = { order: sortDir === 'asc' ? 'CreatedAscending' : 'CreatedDescending' };
  if (statusFilter) filters.status = statusFilter;
  return filters;
}

/** Reads every page of mission summaries at the server page cap. */
export async function loadAllMissionSummaries(fetchPage: MissionPageFetcher, filters: Record<string, string>): Promise<MissionSummary[]> {
  const rows: MissionSummary[] = [];
  let pageNumber = 1;
  let totalPages = 1;
  do {
    const result = await fetchPage({ pageNumber, pageSize: MISSION_SUMMARY_PAGE_CAP, filters });
    rows.push(...(result.objects || []));
    totalPages = result.totalPages || 1;
    pageNumber++;
  } while (pageNumber <= totalPages);
  return rows;
}

export function filterMissions(rows: MissionSummary[], filters: MissionColumnFilters): MissionSummary[] {
  const title = filters.title.toLowerCase();
  const status = filters.status.toLowerCase();
  const branch = filters.branch.toLowerCase();
  return rows.filter(m =>
    (!title || m.title.toLowerCase().includes(title)) &&
    (!status || (m.status ?? '').toLowerCase().includes(status)) &&
    (!branch || (m.branchName ?? '').toLowerCase().includes(branch)),
  );
}

export function sortMissions(rows: MissionSummary[], field: MissionSortField, dir: MissionSortDir): MissionSummary[] {
  const sorted = [...rows];
  sorted.sort((a, b) => {
    let va: string | number = '';
    let vb: string | number = '';
    switch (field) {
      case 'title': va = a.title.toLowerCase(); vb = b.title.toLowerCase(); break;
      case 'status': va = (a.status ?? '').toLowerCase(); vb = (b.status ?? '').toLowerCase(); break;
      case 'priority': va = a.priority; vb = b.priority; break;
      case 'createdUtc': va = a.createdUtc; vb = b.createdUtc; break;
    }
    if (va < vb) return dir === 'asc' ? -1 : 1;
    if (va > vb) return dir === 'asc' ? 1 : -1;
    return 0;
  });
  return sorted;
}
