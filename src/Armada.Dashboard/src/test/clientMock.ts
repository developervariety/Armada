import { listAllPages } from '../lib/listAllPages';
import type { EnumerationResult } from '../types/models';

type ListCall = (params?: Record<string, unknown>) => Promise<EnumerationResult<unknown>>;

/** The listAll wrappers the client builds on a list call, and whether each takes filters or a query object. */
const ALL_PAGES_WRAPPERS: Record<string, 'filters' | 'query'> = {
  listTenants: 'filters',
  listUsers: 'filters',
  listCredentials: 'filters',
  listFleets: 'filters',
  listVessels: 'filters',
  listCaptains: 'filters',
  listMissions: 'filters',
  listVoyages: 'filters',
  listPromptTemplates: 'filters',
  listPlaybooks: 'filters',
  listWorkflowProfiles: 'filters',
  listCheckRuns: 'filters',
  listPersonas: 'filters',
  listPipelines: 'filters',
  listDocks: 'filters',
  listProjectProfiles: 'filters',
  listSkills: 'filters',
  listObjectives: 'query',
  listEnvironments: 'query',
  listDeployments: 'query',
  listRunbooks: 'query',
  listRunbookExecutions: 'query',
  listReleases: 'query',
};

/** The page size the client's listAll wrappers ask for. */
export const ALL_PAGES_SIZE = 1000;

/**
 * Complete an api/client mock: for every mocked list call that has a listAll wrapper, add that wrapper reading
 * every page of the mocked call the way the client does. A test then serves pages from the list mock and
 * proves the page under test reads all of them. Wrappers the mock already defines are kept.
 */
export function withAllPages<T extends Record<string, unknown>>(mocks: T): T {
  const completed: Record<string, unknown> = { ...mocks };
  for (const [name, shape] of Object.entries(ALL_PAGES_WRAPPERS)) {
    const list = mocks[name] as ListCall | undefined;
    const wrapper = name.replace(/^list/, 'listAll');
    if (!list || wrapper in completed) continue;
    completed[wrapper] = shape === 'query'
      ? (query?: Record<string, unknown>) => listAllPages((pageNumber) => list({ ...query, pageNumber, pageSize: ALL_PAGES_SIZE }))
      : (filters?: Record<string, string>) => listAllPages((pageNumber) => list(filters ? { pageNumber, pageSize: ALL_PAGES_SIZE, filters } : { pageNumber, pageSize: ALL_PAGES_SIZE }));
  }
  return completed as T;
}

/**
 * A list-call implementation that pages records the way the server does: ten per page when the caller sends
 * no page size, and never more than `cap` per page however many the caller asks for.
 */
export function servesPages<T>(records: T[], cap = 1000) {
  return async (params?: { pageNumber?: number; pageSize?: number }) => {
    const pageSize = Math.min(params?.pageSize || 10, cap);
    const pageNumber = params?.pageNumber || 1;
    const start = (pageNumber - 1) * pageSize;
    return {
      success: true,
      pageNumber,
      pageSize,
      totalPages: Math.ceil(records.length / pageSize),
      totalRecords: records.length,
      totalMs: 1,
      objects: records.slice(start, start + pageSize),
    };
  };
}
