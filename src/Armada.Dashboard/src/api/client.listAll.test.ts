import { afterEach, expect, test, vi } from 'vitest';
import {
  listAllCaptains,
  listAllCheckRuns,
  listAllCredentials,
  listAllDeployments,
  listAllDocks,
  listAllEnvironments,
  listAllFleets,
  listAllMissions,
  listAllObjectives,
  listAllPersonas,
  listAllPipelines,
  listAllPlaybooks,
  listAllProjectProfiles,
  listAllPromptTemplates,
  listAllReleases,
  listAllRunbookExecutions,
  listAllRunbooks,
  listAllSkills,
  listAllTenants,
  listAllUsers,
  listAllVessels,
  listAllVoyages,
  listAllWorkflowProfiles,
} from './client';

afterEach(() => {
  vi.unstubAllGlobals();
});

/** Routes whose server page cap is 500 rather than the enumeration default of 1000. */
const CAP_500_PATHS = new Set(['/api/v1/objectives', '/api/v1/runbooks', '/api/v1/runbook-executions']);

/** Serves an enumeration the way the server pages it: default page size 10, capped at the route's limit. */
function stubEnumeration(total: number) {
  const requests: URL[] = [];
  vi.stubGlobal('fetch', vi.fn(async (input: string) => {
    const url = new URL(input, 'http://localhost');
    requests.push(url);
    const cap = CAP_500_PATHS.has(url.pathname) ? 500 : 1000;
    const pageSize = Math.min(Number(url.searchParams.get('pageSize') || 10), cap);
    const pageNumber = Number(url.searchParams.get('pageNumber') || 1);
    const start = (pageNumber - 1) * pageSize;
    const objects = Array.from({ length: Math.max(0, Math.min(pageSize, total - start)) }, (_, index) => ({ id: `rec_${start + index}`, name: `Record ${start + index}` }));
    const body = { success: true, pageNumber, pageSize, totalPages: Math.ceil(total / pageSize), totalRecords: total, totalMs: 1, objects };
    return new Response(JSON.stringify(body), { status: 200 });
  }));
  return requests;
}

type ListAll = () => Promise<Array<{ id: string }>>;

test.each([
  ['tenants', listAllTenants, '/api/v1/tenants'],
  ['users', listAllUsers, '/api/v1/users'],
  ['credentials', listAllCredentials, '/api/v1/credentials'],
  ['fleets', listAllFleets, '/api/v1/fleets'],
  ['vessels', listAllVessels, '/api/v1/vessels'],
  ['objectives', listAllObjectives, '/api/v1/objectives'],
  ['captains', listAllCaptains, '/api/v1/captains'],
  ['missions', listAllMissions, '/api/v1/missions'],
  ['voyages', listAllVoyages, '/api/v1/voyages'],
  ['prompt templates', listAllPromptTemplates, '/api/v1/prompt-templates'],
  ['playbooks', listAllPlaybooks, '/api/v1/playbooks'],
  ['workflow profiles', listAllWorkflowProfiles, '/api/v1/workflow-profiles'],
  ['environments', listAllEnvironments, '/api/v1/environments'],
  ['deployments', listAllDeployments, '/api/v1/deployments'],
  ['runbooks', listAllRunbooks, '/api/v1/runbooks'],
  ['runbook executions', listAllRunbookExecutions, '/api/v1/runbook-executions'],
  ['releases', listAllReleases, '/api/v1/releases'],
  ['check runs', listAllCheckRuns, '/api/v1/check-runs'],
  ['personas', listAllPersonas, '/api/v1/personas'],
  ['pipelines', listAllPipelines, '/api/v1/pipelines'],
  ['docks', listAllDocks, '/api/v1/docks'],
  ['project profiles', listAllProjectProfiles, '/api/v1/project-profiles'],
  ['skills', listAllSkills, '/api/v1/skills'],
] as const)('reads every page of %s instead of the first server page', async (_name, listAll, path) => {
  const requests = stubEnumeration(1005);

  const all = await (listAll as unknown as ListAll)();

  const pages = CAP_500_PATHS.has(path) ? ['1', '2', '3'] : ['1', '2'];
  expect(all).toHaveLength(1005);
  expect(new Set(all.map((record) => record.id)).size).toBe(1005);
  expect(requests.map((url) => url.pathname)).toEqual(pages.map(() => path));
  expect(requests.map((url) => url.searchParams.get('pageNumber'))).toEqual(pages);
  expect(requests.every((url) => url.searchParams.get('pageSize') === '1000')).toBe(true);
});

test('a filtered read sends its filter with every page', async () => {
  const requests = stubEnumeration(1005);

  await listAllVessels({ fleetId: 'flt_1' });
  await listAllMissions({ voyageId: 'vyg_1' });
  await listAllCheckRuns({ missionId: 'msn_1' });
  await listAllDeployments({ releaseId: 'rel_1' });
  await listAllEnvironments({ vesselId: 'vsl_1' });
  await listAllRunbookExecutions({ incidentId: 'inc_1' });
  await listAllObjectives({ releaseId: 'rel_1' });

  const filterOf = (path: string, key: string) =>
    requests.filter((url) => url.pathname === path).map((url) => url.searchParams.get(key));
  expect(filterOf('/api/v1/vessels', 'fleetId')).toEqual(['flt_1', 'flt_1']);
  expect(filterOf('/api/v1/missions', 'voyageId')).toEqual(['vyg_1', 'vyg_1']);
  expect(filterOf('/api/v1/check-runs', 'missionId')).toEqual(['msn_1', 'msn_1']);
  expect(filterOf('/api/v1/deployments', 'releaseId')).toEqual(['rel_1', 'rel_1']);
  expect(filterOf('/api/v1/environments', 'vesselId')).toEqual(['vsl_1', 'vsl_1']);
  expect(filterOf('/api/v1/runbook-executions', 'incidentId')).toEqual(['inc_1', 'inc_1', 'inc_1']);
  expect(filterOf('/api/v1/objectives', 'releaseId')).toEqual(['rel_1', 'rel_1', 'rel_1']);
});
