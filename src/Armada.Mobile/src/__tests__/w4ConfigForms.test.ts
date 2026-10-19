import type { ModelEndpoint, WorkflowProfile } from '@dashboard/types/models';
import type { ScopeViewer } from '@dashboard/lib/scoping';
import { countProfileCapabilities, formatHealthSpan, formatStages, splitList, unsupportedEndpointReason } from '@dashboard/lib/configuration';
import { scopeValue } from '../screens/configuration/common';
import { addHeaderRow, endpointFields, endpointPayload, endpointSentAs, endpointValidate, endpointValues, removeHeaderRow, type AuthModelEndpoint } from '../screens/configuration/endpointForm';
import { memoryFilters, memoryPayload, memoryValues } from '../screens/configuration/MemoriesTab';
import { moveStage, stageEntries, stagesPayload } from '../screens/configuration/pipelineForm';
import { applyWorkflowValues, blankWorkflowProfile, projectPayload, projectValues, blankProjectProfile, workflowPayload, workflowValues } from '../screens/configuration/profileForms';
import { playbookStats } from '../screens/configuration/simpleForms';

const ADMIN: ScopeViewer = { isAdmin: false, isTenantAdmin: true, tenantId: 'ten_1', userId: 'usr_1' };
const USER: ScopeViewer = { isAdmin: false, isTenantAdmin: false, tenantId: 'ten_1', userId: 'usr_1' };

describe('configuration form logic', () => {
  it.each(['OllamaCloud', 'XAI'])('%s is selectable and requires credentials', (provider) => {
    const t = ((key: string) => key) as Parameters<typeof endpointValidate>[0];
    const values: ReturnType<typeof endpointValues> = { ...endpointValues(ADMIN, null), provider };
    expect(endpointValidate(t, values, null)).toBe('API key is required.');
    values.apiKey = 'test-token';
    expect(endpointValidate(t, values, null)).toBeNull();
    const fields = endpointFields(t, ADMIN, values, null);
    const providerField = fields.find(f => f.key === 'provider');
    expect(providerField?.kind === 'select' && providerField.options.some(o => o.value === provider)).toBe(true);
    const payload = endpointPayload(ADMIN, values, null);
    expect(payload.authType).toBe('ProviderDefault');
    if (provider === 'OllamaCloud') expect(payload.baseUrl).toBe('https://ollama.com:443');
  });
  it('scope: admins choose, users create personal and keep the record scope on edit', () => {
    expect(scopeValue(ADMIN, { scope: 'UserSpecific' })).toBe('UserSpecific');
    expect(scopeValue(ADMIN, {})).toBe('TenantWide');
    expect(scopeValue(USER, { scope: 'TenantWide' })).toBe('UserSpecific');
    expect(scopeValue(USER, { scope: 'UserSpecific' }, 'TenantWide')).toBe('TenantWide');
  });

  it('pipeline stages: blanks dropped, order renumbered, moves bounded', () => {
    const stages = stageEntries([
      { id: 's2', pipelineId: 'p', order: 2, personaName: 'Worker', isOptional: false, description: null, requiresReview: true, reviewDenyAction: 'FailPipeline' },
      { id: 's1', pipelineId: 'p', order: 1, personaName: 'Architect', isOptional: true, description: 'Plan', requiresReview: false, reviewDenyAction: 'RetryStage' },
    ]);
    expect(stages.map((s) => s.personaName)).toEqual(['Architect', 'Worker']);
    const moved = moveStage(stages, 1, -1);
    expect(moved.map((s) => s.personaName)).toEqual(['Worker', 'Architect']);
    expect(moveStage(stages, 0, -1)).toBe(stages);
    expect(stagesPayload([...moved, { ...moved[0], personaName: '  ' }])).toEqual([
      { personaName: 'Worker', isOptional: false, description: null, requiresReview: true, reviewDenyAction: 'FailPipeline', order: 1 },
      { personaName: 'Architect', isOptional: true, description: 'Plan', requiresReview: false, reviewDenyAction: 'RetryStage', order: 2 },
    ]);
    expect(formatStages([])).toBe('-');
  });

  it('workflow profiles: the editor payload trims, nulls blanks, and keeps only the scope target', () => {
    const base = blankWorkflowProfile(ADMIN, { scope: 'Vessel', vesselId: 'vsl_1' });
    expect(base.scope).toBe('Vessel');
    const v = { ...workflowValues(base), name: '  Build  ', buildCommand: '  dotnet build ', lintCommand: '  ', fleetId: 'flt_1', languageHints: 'dotnet, react' };
    const next = applyWorkflowValues(ADMIN, base, v, false);
    next.environments = [{ environmentName: ' prod ', deployCommand: ' deploy ', rollbackCommand: '', smokeTestCommand: null, healthCheckCommand: null, deploymentVerificationCommand: null, rollbackVerificationCommand: null }, { environmentName: ' ', deployCommand: null, rollbackCommand: null, smokeTestCommand: null, healthCheckCommand: null, deploymentVerificationCommand: null, rollbackVerificationCommand: null }];
    next.requiredInputs = [{ provider: 'EnvironmentVariable', key: ' TOKEN ', environmentName: '', description: '' }, { provider: 'FilePath', key: '  ' }];
    const payload = workflowPayload(next);
    expect(payload).toMatchObject({ name: 'Build', buildCommand: 'dotnet build', lintCommand: null, vesselId: 'vsl_1', fleetId: null, languageHints: ['dotnet', 'react'], ownershipScope: 'TenantWide' });
    expect(payload.environments).toEqual([{ environmentName: 'prod', deployCommand: 'deploy', rollbackCommand: null, smokeTestCommand: null, healthCheckCommand: null, deploymentVerificationCommand: null, rollbackVerificationCommand: null }]);
    expect(payload.requiredInputs).toEqual([{ provider: 'EnvironmentVariable', key: 'TOKEN', environmentName: null, description: null }]);
    expect(countProfileCapabilities({ ...base, buildCommand: 'b', environments: [{ environmentName: 'p', deployCommand: 'd', rollbackCommand: null, smokeTestCommand: 's', healthCheckCommand: null, deploymentVerificationCommand: 'x', rollbackVerificationCommand: null }] } as WorkflowProfile)).toBe(3);
  });

  it('project profiles: skills split and overrides kept', () => {
    const values = { ...projectValues(blankProjectProfile(USER)), scope: 'Fleet', fleetId: 'flt_1', vesselId: 'vsl_9', skills: 'dotnet\ntdd, perf' };
    expect(projectPayload(USER, values, [{ personaName: ' Worker ', promptTemplateName: '', additionalInstructions: 'x', enabled: true }], null)).toMatchObject({
      scope: 'Fleet', fleetId: 'flt_1', vesselId: null, skills: ['dotnet', 'tdd', 'perf'], ownershipScope: 'UserSpecific',
      personaOverrides: [{ personaName: 'Worker', promptTemplateName: null, additionalInstructions: 'x', enabled: true }],
    });
    expect(splitList(' a,\nb , ')).toEqual(['a', 'b']);
  });

  it('endpoints: the credential is sent only when typed; provider guard', () => {
    const existing = { id: 'mep_1', scope: 'TenantWide', name: 'E', kind: 'Embedding', provider: 'OpenAI', baseUrl: 'https://x', hasApiKey: true, dimensionality: 3, timeoutMs: 1000, enabled: true } as ModelEndpoint;
    const v = endpointValues(ADMIN, existing);
    expect(v.apiKey).toBe('');
    expect(endpointPayload(ADMIN, v, existing)).not.toHaveProperty('apiKey');
    expect(endpointPayload(ADMIN, { ...v, apiKey: 'sk-new' }, existing).apiKey).toBe('sk-new');
    expect(unsupportedEndpointReason('Anthropic', 'Embedding')).toMatch(/embeddings API/);
    expect(unsupportedEndpointReason('OpenAI', 'Embedding')).toBeNull();
    expect(formatHealthSpan('2026-10-01T00:00:00Z', Date.parse('2026-10-02T03:04:00Z'))).toBe('1d 3h');
    expect(formatHealthSpan(null)).toBe('-');
  });

  it('endpoints: auth types, per-type fields, secret labels, and the sent-as description', () => {
    const t = (text: string, params?: Record<string, string | number | null | undefined>) => text.replace(/\{\{(\w+)\}\}/g, (_m, k: string) => String(params?.[k] ?? ''));
    const base = endpointValues(ADMIN, null);
    expect(base.authType).toBe('ProviderDefault');
    const labels = (v: Record<string, string | boolean>) => endpointFields(t, ADMIN, v, null).map((f) => f.label);
    expect(labels(base)).toContain('Authentication');
    expect(labels(base)).toContain('API Key');
    expect(endpointSentAs(t, base)).toBe('Sent as: Authorization: Bearer <key>');
    expect(endpointSentAs(t, { ...base, provider: 'Anthropic' })).toBe('Sent as: x-api-key: <key>');
    expect(endpointSentAs(t, { ...base, provider: 'Gemini' })).toBe('Sent as: x-goog-api-key: <key>');
    expect(endpointSentAs(t, { ...base, provider: 'AzureOpenAI' })).toBe('Sent as: api-key: <key>');
    expect(labels({ ...base, authType: 'Bearer' })).toContain('Bearer token');
    expect(labels({ ...base, authType: 'Basic' })).toEqual(expect.arrayContaining(['Username', 'Password']));
    expect(labels({ ...base, authType: 'Header' })).toEqual(expect.arrayContaining(['Header name', 'Header value']));
    expect(labels({ ...base, authType: 'QueryParameter' })).toEqual(expect.arrayContaining(['Query parameter name', 'API Key']));
    expect(endpointSentAs(t, { ...base, authType: 'Header', authHeaderName: 'X-Token' })).toBe('Sent as: X-Token: <value>');
    expect(endpointSentAs(t, { ...base, authType: 'QueryParameter', authQueryParameter: 'key' })).toBe('Sent as: ?key=<key>');
    // None hides the secret and never sends it.
    const none = { ...base, authType: 'None', apiKey: 'typed' };
    expect(labels(none)).not.toContain('API Key');
    expect(endpointSentAs(t, none)).toBe('No credential is sent.');
    expect(endpointPayload(ADMIN, none, null)).not.toHaveProperty('apiKey');
    // VertexAI and Bedrock allow only ProviderDefault: no selector, and the payload forces it.
    const vertex = { ...base, provider: 'VertexAI', authType: 'Bearer' };
    expect(endpointFields(t, ADMIN, vertex, null).some((f) => f.key === 'authType')).toBe(false);
    expect(labels(vertex)).toContain('Service Account JSON');
    expect(endpointPayload(ADMIN, vertex, null).authType).toBe('ProviderDefault');
    expect(labels({ ...base, provider: 'Bedrock', authType: 'Basic' })).toContain('AWS Secret Access Key');
    // Validation per type.
    expect(endpointValidate(t, { ...base, authType: 'Basic' }, null)).toBe('Basic authentication needs a username.');
    expect(endpointValidate(t, { ...base, authType: 'Header' }, null)).toBe('Header authentication needs a header name.');
    expect(endpointValidate(t, { ...base, authType: 'QueryParameter' }, null)).toBe('Query parameter authentication needs a parameter name.');
    expect(endpointValidate(t, { ...base, authType: 'Basic', authUsername: 'svc' }, null)).toBeNull();
    // Only the active type's field is sent.
    expect(endpointPayload(ADMIN, { ...base, authType: 'Basic', authUsername: ' svc ', authHeaderName: 'X', apiKey: 'pw' }, null)).toMatchObject({
      authType: 'Basic', authUsername: 'svc', authHeaderName: null, authQueryParameter: null, apiKey: 'pw', headers: [],
    });
  });

  it('endpoints: extra headers add, remove, keep stored values, and validate', () => {
    const t = (text: string, params?: Record<string, string | number | null | undefined>) => text.replace(/\{\{(\w+)\}\}/g, (_m, k: string) => String(params?.[k] ?? ''));
    const existing = {
      id: 'mep_1', scope: 'TenantWide', name: 'E', kind: 'Inference', provider: 'OpenAICompatible', baseUrl: 'https://x', hasApiKey: true, dimensionality: 0, timeoutMs: 1000, enabled: true,
      authType: 'Header', authHeaderName: 'X-Token', headers: [{ name: 'X-Org', value: null, hasValue: true }, { name: 'X-Team', value: null, hasValue: true }],
    } as AuthModelEndpoint;
    const v = endpointValues(ADMIN, existing);
    expect(v).toMatchObject({ authType: 'Header', authHeaderName: 'X-Token', headerCount: '2', headerName0: 'X-Org', headerValue0: '', headerName1: 'X-Team' });
    // Unchanged: stored values are kept by sending null.
    expect(endpointPayload(ADMIN, v, existing).headers).toEqual([{ name: 'X-Org', value: null }, { name: 'X-Team', value: null }]);
    expect(endpointValidate(t, v, existing)).toBeNull();
    // Remove the first row; add a new one that needs a value.
    const removed = removeHeaderRow(v, 0);
    expect(removed).toMatchObject({ headerCount: '1', headerName0: 'X-Team' });
    expect(removed).not.toHaveProperty('headerName1');
    const added = { ...addHeaderRow(removed), headerName1: 'X-New' };
    expect(endpointValidate(t, added, existing)).toBe('Header "X-New" needs a value.');
    expect(endpointPayload(ADMIN, { ...added, headerValue1: 'n' }, existing).headers).toEqual([{ name: 'X-Team', value: null }, { name: 'X-New', value: 'n' }]);
    expect(endpointValidate(t, { ...added, headerName1: 'x-team', headerValue1: 'n' }, existing)).toBe('Header "x-team" is listed more than once.');
    expect(endpointValidate(t, { ...added, headerName1: '', headerValue1: 'n' }, existing)).toBe('Each extra header needs a name.');
    expect(endpointValidate(t, { ...added, headerName1: 'Bad Name', headerValue1: 'n' }, existing)).toMatch(/cannot contain spaces/);
    // Removing every row replaces the list with an empty one; a record without headers omits the list.
    expect(endpointPayload(ADMIN, removeHeaderRow(removed, 0), existing).headers).toEqual([]);
    const plain = { ...existing, headers: [] } as AuthModelEndpoint;
    expect(endpointPayload(ADMIN, endpointValues(ADMIN, plain), plain)).not.toHaveProperty('headers');
  });

  it('memories and playbooks', () => {
    expect(memoryFilters('Semantic', '  db ')).toEqual({ type: 'Semantic', search: 'db' });
    expect(memoryFilters('', ' ')).toEqual({});
    expect(memoryPayload(ADMIN, { ...memoryValues(ADMIN, null), content: 'c', salience: 'x', tags: 'a, b' }, null)).toMatchObject({ salience: 0.5, tags: ['a', 'b'], topic: null, vesselId: null, scope: 'TenantWide' });
    expect(playbookStats('# A\ntext\n## B')).toEqual({ characters: 13, lines: 3, headings: 2 });
    expect(playbookStats('')).toEqual({ characters: 0, lines: 0, headings: 0 });
  });
});
