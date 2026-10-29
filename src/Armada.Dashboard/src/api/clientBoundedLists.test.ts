import { getInbox, getVesselBranches, listCliPermissionRequests, listHarbors, setAuthToken } from './client';
import { onlyCallArgs } from '../test/mockCalls';

function page(objects: unknown[], totalRecords: number, extra: Record<string, unknown> = {}) {
  return new Response(JSON.stringify({
    Success: true, PageNumber: 1, PageSize: 1000, TotalPages: Math.ceil(totalRecords / 1000), TotalRecords: totalRecords, Objects: objects, ...extra,
  }), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

describe('array-returning list helpers read one bounded page', () => {
  beforeEach(() => setAuthToken('tok'));
  afterEach(() => vi.unstubAllGlobals());

  it('listHarbors and getInbox send one POST of PageSize 1000 even when more pages exist', async () => {
    const full = Array.from({ length: 1000 }, (_, i) => ({ Id: `hbr_${i}` }));
    for (const call of [listHarbors, getInbox]) {
      const fetchMock = vi.fn().mockImplementation(async () => page(full, 5000));
      vi.stubGlobal('fetch', fetchMock);
      const rows = await call();
      expect(rows).toHaveLength(1000);
      expect(fetchMock).toHaveBeenCalledTimes(1);
      const [, init] = onlyCallArgs(fetchMock) as [string, RequestInit];
      expect(init.method).toBe('POST');
      expect(JSON.parse(String(init.body))).toMatchObject({ PageNumber: 1, PageSize: 1000 });
    }
  });

  it('getVesselBranches reads one page and reports the true branch count', async () => {
    const fetchMock = vi.fn().mockImplementation(async () => page(Array.from({ length: 1000 }, (_, i) => ({ Name: `b${i}` })), 1500, { VesselId: 'vsl_1', DefaultBranch: 'main' }));
    vi.stubGlobal('fetch', fetchMock);
    const result = await getVesselBranches('vsl_1');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(result.branches).toHaveLength(1000);
    expect(result.branchCount).toBe(1500);
    expect(result.defaultBranch).toBe('main');
  });

  it('listCliPermissionRequests posts one page to the enumerate route with the limit as the page size', async () => {
    const fetchMock = vi.fn().mockResolvedValue(page([{ Id: 'cpr_1' }], 1));
    vi.stubGlobal('fetch', fetchMock);
    const rows = await listCliPermissionRequests({ status: 'Pending', threadId: 'ath_1', limit: 200 });
    expect(rows).toHaveLength(1);
    const [url, init] = onlyCallArgs(fetchMock) as [string, RequestInit];
    expect(url).toContain('/api/v1/cli-permissions/requests/enumerate');
    expect(init.method).toBe('POST');
    expect(JSON.parse(String(init.body))).toMatchObject({ Status: 'Pending', ThreadId: 'ath_1', PageNumber: 1, PageSize: 200 });
  });
});
