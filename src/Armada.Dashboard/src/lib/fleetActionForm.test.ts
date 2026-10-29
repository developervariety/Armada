import type { FleetAction, FleetActionRun } from '../types/models';
import {
  adHocFromDefinition,
  buildActionUpsertPayload,
  buildAdHocDefinition,
  buildSavedRunRequest,
  definitionFromRun,
  emptyAdHoc,
  formFromAction,
  runProgress,
  validateActionForm,
} from './fleetActionForm';

const action: FleetAction = {
  id: 'fac_1', tenantId: 'ten', userId: 'usr', name: 'Build', description: 'Runs the build', kind: 'Command',
  commandText: 'dotnet build', promptTemplate: null, pipelineId: null, persona: null,
  timeoutSeconds: 600, defaultConcurrency: 8, requiresCleanWorkingTree: false, isBuiltIn: false, builtInKey: null, active: true,
  createdUtc: '2026-10-01T00:00:00Z', lastUpdateUtc: '2026-10-01T00:00:00Z',
};

describe('fleet action form', () => {
  it('starts blank, copies a source with a suffix, or edits it as is', () => {
    expect(formFromAction(null, 'create', 300, '(copy)')).toMatchObject({ name: '', kind: 'Command', timeoutSeconds: '300', defaultConcurrency: '4', requiresCleanWorkingTree: true });
    expect(formFromAction(action, 'create', 300, '(copy)').name).toBe('Build (copy)');
    expect(formFromAction(action, 'edit', 300, '(copy)')).toMatchObject({ name: 'Build', timeoutSeconds: '600', defaultConcurrency: '8', requiresCleanWorkingTree: false });
  });

  it('validates name, body, timeout, and concurrency', () => {
    const errs = validateActionForm({ ...formFromAction(null, 'create', 300, ''), timeoutSeconds: '2', defaultConcurrency: '33' });
    expect(Object.keys(errs).sort()).toEqual(['body', 'concurrency', 'name', 'timeout']);
    expect(validateActionForm({ ...formFromAction(action, 'edit', 300, '') })).toEqual({});
    // Mission actions have no timeout.
    expect(validateActionForm({ ...formFromAction(null, 'create', 300, ''), name: 'm', kind: 'Mission', promptTemplate: 'p', timeoutSeconds: '1' })).toEqual({});
  });

  it('builds create bodies with nulls and edit bodies with empty strings for cleared optionals', () => {
    const form = { ...formFromAction(action, 'edit', 300, ''), description: ' ' };
    expect(buildActionUpsertPayload(form, 'edit')).toEqual({
      Name: 'Build', Description: '', Kind: 'Command', CommandText: 'dotnet build', PromptTemplate: null, PipelineId: '', Persona: '',
      TimeoutSeconds: 600, DefaultConcurrency: 8, RequiresCleanWorkingTree: false,
    });
    const mission = { ...formFromAction(null, 'create', 300, ''), name: 'm', kind: 'Mission' as const, promptTemplate: 'p', pipelineId: 'ppl_1', requiresCleanWorkingTree: true };
    expect(buildActionUpsertPayload(mission, 'create')).toMatchObject({ Description: null, CommandText: null, PromptTemplate: 'p', PipelineId: 'ppl_1', Persona: null, TimeoutSeconds: null, RequiresCleanWorkingTree: false });
  });
});

describe('fleet action run flow', () => {
  it('sends the clean-tree override only when it differs on a Command action', () => {
    expect(buildSavedRunRequest(['vsl_1'], 4, action, null)).toEqual({ VesselIds: ['vsl_1'], Concurrency: 4 });
    expect(buildSavedRunRequest(['vsl_1'], 4, action, false)).toEqual({ VesselIds: ['vsl_1'], Concurrency: 4 });
    expect(buildSavedRunRequest(['vsl_1'], 4, action, true)).toEqual({ VesselIds: ['vsl_1'], Concurrency: 4, Overrides: { RequiresCleanWorkingTree: true } });
    expect(buildSavedRunRequest(['vsl_1'], 4, { ...action, kind: 'Mission' }, true).Overrides).toBeUndefined();
  });

  it('round-trips an ad hoc definition and rebuilds one from a run snapshot', () => {
    const adHoc = { ...emptyAdHoc('Command'), name: ' Status ', commandText: 'git status', timeoutSeconds: '60' };
    const def = buildAdHocDefinition(adHoc);
    expect(def).toEqual({ Name: 'Status', Kind: 'Command', CommandText: 'git status', PromptTemplate: null, PipelineId: null, TimeoutSeconds: 60, RequiresCleanWorkingTree: true });
    expect(adHocFromDefinition(def)).toEqual({ ...adHoc, name: 'Status' });
    expect(adHocFromDefinition({ Kind: 'Mission' }).requiresCleanWorkingTree).toBe(false);
    const run = { actionName: 'Fix', kind: 'Mission', commandText: null, promptTemplate: 'Fix it', pipelineId: 'ppl_1', timeoutSeconds: 300, requiresCleanWorkingTree: false } as FleetActionRun;
    expect(definitionFromRun(run)).toEqual({ Name: 'Fix', Kind: 'Mission', CommandText: null, PromptTemplate: 'Fix it', PipelineId: 'ppl_1', TimeoutSeconds: 300, RequiresCleanWorkingTree: false });
  });

  it('computes progress', () => {
    expect(runProgress({ targetCount: 8, succeededCount: 3, failedCount: 1, skippedCount: 1, cancelledCount: 1 })).toEqual({ total: 8, done: 6, percent: 75 });
    expect(runProgress({ targetCount: 0, succeededCount: 0, failedCount: 0, skippedCount: 0, cancelledCount: 0 }).percent).toBe(0);
  });
});

// Findings finish a target without inflating successful or failed counts.
it('includes Findings in completed progress', () => {
  expect(runProgress({ targetCount: 4, succeededCount: 1, failedCount: 0, skippedCount: 1, cancelledCount: 0, findingsCount: 2 })).toEqual({ total: 4, done: 4, percent: 100 });
});

it('retains scan, tool, platform and typed input snapshots through an ad hoc rerun', () => {
  const run: FleetActionRun = {
    id: 'far_scan', tenantId: null, userId: null, actionId: null, promptTemplate: null, pipelineId: null, persona: null,
    concurrency: 1, status: 'Completed', targetCount: 1, succeededCount: 1, failedCount: 0, skippedCount: 0, cancelledCount: 0,
    startedUtc: null, completedUtc: null, createdUtc: '2026-10-27T00:00:00Z', lastUpdateUtc: '2026-10-27T00:00:00Z',
    actionName: 'Scan', kind: 'Command', commandText: 'scan {{input.version}}', commandWindows: 'scan.exe {{input.version}}',
    outputParser: 'npm-audit-json', requiredTools: ['scan'], requiresNetwork: true, scanSourceActionKey: 'prior-scan',
    inputs: [{ name: 'version', type: 'semver', required: true, default: '1.0.0', allowedValues: [], description: 'Release version' }],
    inputValues: { version: '2.0.0' }, timeoutSeconds: 60, requiresCleanWorkingTree: false,
  };
  const rerun = buildAdHocDefinition(adHocFromDefinition(definitionFromRun(run)));
  expect(rerun).toMatchObject({ CommandWindows: 'scan.exe {{input.version}}', OutputParser: 'npm-audit-json', RequiredTools: ['scan'], RequiresNetwork: true, ScanSourceActionKey: 'prior-scan', Inputs: [{ Name: 'version', Type: 'semver', Required: true, Default: '2.0.0' }] });
});
