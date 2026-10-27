import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  classifyStageOutcome,
  createFailureCauseRule,
  deleteFailureCauseRule,
  draftFailureCauseRule,
  getFailureCauseRule,
  getStageOutcome,
  listFailureCauseRules,
  listStageOutcomes,
  reclassifyStageOutcomes,
  setAuthToken,
  updateFailureCauseRule,
} from './client';
import { onlyCallArgs } from '../test/mockCalls';

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function stub(status: number, body: unknown) {
  const fetchMock = vi.fn().mockResolvedValue(jsonResponse(status, body));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const OUTCOME = { Id: 'sto_1', MissionId: 'msn_1', Disposition: 'Failed', SuspectedCause: 'Protocol', CauseSource: 'Manual', ClassifiedByUserId: 'usr_1' };
const RULE = {
  Id: 'fcr_1', TenantId: null, Name: 'Prose', Priority: 5, Enabled: true, IsBuiltIn: false, Cause: 'Protocol',
  Match: { Personas: [], Runtimes: [], FailureKinds: ['InvalidOutput'], RuntimeFailureKinds: [], StageCheckStatuses: [], StageCheckReasons: [], Dispositions: [], TextPattern: null },
};

describe('stage outcome client', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    setAuthToken(null);
  });

  it('lists outcomes with the filters on the querystring and the session token', async () => {
    setAuthToken('tok_1');
    const fetchMock = stub(200, { Success: true, PageNumber: 2, PageSize: 25, TotalPages: 2, TotalRecords: 26, Objects: [OUTCOME] });
    const page = await listStageOutcomes({ persona: 'Worker', suspectedCause: 'Protocol', isEvaluation: false, pageNumber: 2, pageSize: 25, missionId: null });
    const [url, init] = onlyCallArgs(fetchMock);
    const parsed = new URL(url, 'http://localhost');
    expect(parsed.pathname).toBe('/api/v1/stage-outcomes');
    expect(Object.fromEntries(parsed.searchParams)).toEqual({ persona: 'Worker', suspectedCause: 'Protocol', isEvaluation: 'false', pageNumber: '2', pageSize: '25' });
    expect(init.method).toBe('GET');
    expect(init.headers['X-Token']).toBe('tok_1');
    expect(page.totalRecords).toBe(26);
    expect(page.objects[0]).toMatchObject({ id: 'sto_1', suspectedCause: 'Protocol', causeSource: 'Manual' });
  });

  it('lists everything with no query', async () => {
    const fetchMock = stub(200, { Success: true, PageNumber: 1, PageSize: 100, TotalPages: 0, TotalRecords: 0, Objects: [] });
    await listStageOutcomes();
    expect(onlyCallArgs(fetchMock)[0]).toMatch(/\/api\/v1\/stage-outcomes$/);
  });

  it('gets one outcome and surfaces a 404', async () => {
    const fetchMock = stub(200, OUTCOME);
    await expect(getStageOutcome('sto 1')).resolves.toMatchObject({ id: 'sto_1' });
    expect(new URL(onlyCallArgs(fetchMock)[0], 'http://localhost').pathname).toBe('/api/v1/stage-outcomes/sto%201');
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(404, { Error: 'NotFound', Message: 'Stage outcome not found.' })));
    await expect(getStageOutcome('sto_x')).rejects.toThrow('Stage outcome not found.');
  });

  it('classifies with Cause and Note, and hands back with a null cause', async () => {
    let fetchMock = stub(200, OUTCOME);
    await classifyStageOutcome('sto_1', 'Protocol', 'evidence');
    let [url, init] = onlyCallArgs(fetchMock);
    expect(new URL(url, 'http://localhost').pathname).toBe('/api/v1/stage-outcomes/sto_1/cause');
    expect(init.method).toBe('PUT');
    expect(JSON.parse(init.body)).toEqual({ Cause: 'Protocol', Note: 'evidence' });

    fetchMock = stub(200, OUTCOME);
    await classifyStageOutcome('sto_1', null);
    [url, init] = onlyCallArgs(fetchMock);
    expect(JSON.parse(init.body)).toEqual({ Cause: null, Note: null });
  });

  it('reclassifies with a PascalCase filter body and an empty body without one', async () => {
    let fetchMock = stub(200, { Examined: 3, Changed: 1, SkippedManual: 1 });
    const result = await reclassifyStageOutcomes({ suspectedCause: 'Unknown', isEvaluation: false, tenantId: '', pageSize: 5 });
    let [url, init] = onlyCallArgs(fetchMock);
    expect(new URL(url, 'http://localhost').pathname).toBe('/api/v1/stage-outcomes/reclassify');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body)).toEqual({ SuspectedCause: 'Unknown', IsEvaluation: false });
    expect(result).toEqual({ examined: 3, changed: 1, skippedManual: 1 });

    fetchMock = stub(200, { Examined: 0, Changed: 0, SkippedManual: 0 });
    await reclassifyStageOutcomes();
    [url, init] = onlyCallArgs(fetchMock);
    expect(JSON.parse(init.body)).toEqual({});
  });

  it('drafts a rule from an outcome', async () => {
    const fetchMock = stub(200, RULE);
    const draft = await draftFailureCauseRule('sto_1');
    const [url, init] = onlyCallArgs(fetchMock);
    expect(new URL(url, 'http://localhost').pathname).toBe('/api/v1/stage-outcomes/sto_1/rule');
    expect(init.method).toBe('POST');
    expect(draft.match.failureKinds).toEqual(['InvalidOutput']);
  });
});

describe('failure-cause rule client', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('lists one page', async () => {
    const fetchMock = stub(200, { Success: true, PageNumber: 1, PageSize: 50, TotalPages: 1, TotalRecords: 1, Objects: [RULE] });
    const page = await listFailureCauseRules({ pageNumber: 1, pageSize: 50 });
    const parsed = new URL(onlyCallArgs(fetchMock)[0], 'http://localhost');
    expect(parsed.pathname).toBe('/api/v1/failure-cause-rules');
    expect(parsed.searchParams.get('pageSize')).toBe('50');
    expect(page.objects[0]).toMatchObject({ id: 'fcr_1', isBuiltIn: false, cause: 'Protocol' });
  });

  it('creates with a full PascalCase match, filling omitted lists', async () => {
    const fetchMock = stub(201, RULE);
    await createFailureCauseRule({ name: 'Prose', cause: 'Protocol', priority: 5, match: { failureKinds: ['InvalidOutput'], textPattern: 'result block' } });
    const [url, init] = onlyCallArgs(fetchMock);
    expect(new URL(url, 'http://localhost').pathname).toBe('/api/v1/failure-cause-rules');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body)).toEqual({
      Name: 'Prose', Description: null, Cause: 'Protocol', Priority: 5,
      Match: { Personas: [], Runtimes: [], FailureKinds: ['InvalidOutput'], RuntimeFailureKinds: [], StageCheckStatuses: [], StageCheckReasons: [], Dispositions: [], TextPattern: 'result block' },
    });
  });

  it('surfaces the validation message of a 400', async () => {
    stub(400, { Error: 'BadRequest', Message: 'Match must set at least one criterion.' });
    await expect(createFailureCauseRule({ name: 'x', cause: 'Protocol', match: {} })).rejects.toThrow('Match must set at least one criterion.');
  });

  it.each([
    ['reclassify', () => reclassifyStageOutcomes(), '/api/v1/stage-outcomes/reclassify', 'POST'],
    ['global rule update', () => updateFailureCauseRule('fcr_global', { name: 'Global', cause: 'Protocol', match: { failureKinds: ['Timeout'] } }), '/api/v1/failure-cause-rules/fcr_global', 'PUT'],
    ['global rule delete', () => deleteFailureCauseRule('fcr_global'), '/api/v1/failure-cause-rules/fcr_global', 'DELETE'],
  ] as const)('surfaces forbidden %s responses', async (_name, action, path, method) => {
    // Attach the fetch recorder before the action and verify the denied request was actually sent.
    const fetchMock = stub(403, { Error: 'Forbidden', Message: 'Only an admin can perform this action.' });
    await expect(action()).rejects.toThrow('Only an admin can perform this action.');
    const [url, init] = onlyCallArgs(fetchMock);
    expect(new URL(url, 'http://localhost').pathname).toBe(path);
    expect(init.method).toBe(method);
  });

  it('rejects a failed list response instead of treating it as an empty page', async () => {
    const fetchMock = stub(500, { Error: 'InternalError', Message: 'Outcome storage unavailable.' });
    await expect(listStageOutcomes()).rejects.toThrow('Outcome storage unavailable.');
    const [url, init] = onlyCallArgs(fetchMock);
    expect(new URL(url, 'http://localhost').pathname).toBe('/api/v1/stage-outcomes');
    expect(init.method).toBe('GET');
  });

  it('gets, updates and deletes by id', async () => {
    let fetchMock = stub(200, RULE);
    await getFailureCauseRule('fcr_1');
    expect(new URL(onlyCallArgs(fetchMock)[0], 'http://localhost').pathname).toBe('/api/v1/failure-cause-rules/fcr_1');

    fetchMock = stub(200, { ...RULE, Enabled: false });
    const updated = await updateFailureCauseRule('fcr_1', { name: 'Prose', cause: 'Protocol', enabled: false, match: { dispositions: ['Failed'] } });
    const [url, init] = onlyCallArgs(fetchMock);
    expect(new URL(url, 'http://localhost').pathname).toBe('/api/v1/failure-cause-rules/fcr_1');
    expect(init.method).toBe('PUT');
    expect(JSON.parse(init.body)).toMatchObject({ Name: 'Prose', Enabled: false, Match: { Dispositions: ['Failed'] } });
    expect(updated.enabled).toBe(false);

    fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);
    await deleteFailureCauseRule('fcr_1');
    const [delUrl, delInit] = onlyCallArgs(fetchMock);
    expect(new URL(delUrl, 'http://localhost').pathname).toBe('/api/v1/failure-cause-rules/fcr_1');
    expect(delInit.method).toBe('DELETE');
  });
});
