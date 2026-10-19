import { endpointValues, endpointPayload, endpointValidate, endpointFields } from '../screens/configuration/endpointForm';
import type { ModelEndpoint } from '@dashboard/types/models';
import { defaultInferenceOptions } from '@dashboard/lib/inferenceOptions';

const viewer = { isAdmin: true, isTenantAdmin: true, tenantId: 'ten_1', userId: 'usr_1' };
const t = (text: string) => text;

test('mobile exposes inference controls and carries exact policy and zero temperature', () => {
  const values = { ...endpointValues(viewer, null), allowedTools: 'read_file\ngrep', readOnlyTools: true, requireHarbor: true, mcpToolsEnabled: false, maxIterations: '7', maxTokens: '512', temperature: '0', topP: '1' };
  expect(endpointValidate(t, values, null)).toBeNull();
  expect(endpointPayload(viewer, values, null).inferenceOptions).toEqual({ ...defaultInferenceOptions, allowedTools: ['read_file', 'grep'], readOnlyTools: true, requireHarbor: true, mcpToolsEnabled: false, maxIterations: 7, maxTokens: 512, temperature: 0, topP: 1 });
  expect(endpointFields(t, viewer, values, null).map(field => field.key)).toEqual(expect.arrayContaining(['toolsEnabled', 'readOnlyTools', 'mcpToolsEnabled', 'requireHarbor', 'maxIterations', 'maxTokens', 'temperature', 'topP', 'allowedTools']));
});

test('editing an empty allowlist cannot accidentally enable all tools', () => {
  const endpoint = { inferenceOptions: { ...defaultInferenceOptions, allowedTools: [] }, headers: [] } as unknown as ModelEndpoint;
  const values = endpointValues(viewer, endpoint);
  expect(endpointPayload(viewer, values, endpoint).inferenceOptions?.toolsEnabled).toBe(false);
});

test.each<Record<string, string>>([{ maxIterations: '0' }, { maxIterations: '1.5' }, { temperature: 'oops' }, { topP: '2' }, { allowedTools: 'read_file\nread_file' }])('mobile rejects invalid inference options %o', patch => {
  expect(endpointValidate(t, { ...endpointValues(viewer, null), ...patch }, null)).not.toBeNull();
});
