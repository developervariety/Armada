import { describe, expect, it } from 'vitest';
import type { WorkflowProfile } from '../types/models';
import { ALL_CHECK_TYPES, RUNBOOK_CHECK_TYPES, getAvailableCheckTypes } from './deliveryForms';
import { buildWorkflowProfileDuplicatePayload } from './duplicates';
import { countProfileCapabilities } from './configuration';

function profile(overrides: Partial<WorkflowProfile> = {}): WorkflowProfile {
  return {
    id: 'wfp_1',
    name: 'Profile',
    description: null,
    scope: 'Global',
    ownershipScope: 'TenantWide',
    fleetId: null,
    vesselId: null,
    isDefault: false,
    active: true,
    languageHints: [],
    lintCommand: null,
    buildCommand: null,
    unitTestCommand: null,
    abbreviatedUnitTestCommand: null,
    integrationTestCommand: null,
    e2eTestCommand: null,
    migrationCommand: null,
    securityScanCommand: null,
    performanceCommand: null,
    packageCommand: null,
    deploymentVerificationCommand: null,
    rollbackVerificationCommand: null,
    publishArtifactCommand: null,
    releaseVersioningCommand: null,
    changelogGenerationCommand: null,
    requiredSecrets: [],
    requiredInputs: [],
    expectedArtifacts: [],
    environments: [],
    createdUtc: '2026-10-28T00:00:00Z',
    lastUpdateUtc: '2026-10-28T00:00:00Z',
    ...overrides,
  } as WorkflowProfile;
}

describe('full and abbreviated unit-test commands', () => {
  it('offers AbbreviatedUnitTest everywhere the check type lists are used', () => {
    expect(ALL_CHECK_TYPES).toContain('AbbreviatedUnitTest');
    expect(RUNBOOK_CHECK_TYPES).toContain('AbbreviatedUnitTest');
  });

  it('lists each unit-test check type only when its command is set', () => {
    expect(getAvailableCheckTypes(profile({ unitTestCommand: 'dotnet test' }))).toEqual(['UnitTest']);
    expect(getAvailableCheckTypes(profile({ abbreviatedUnitTestCommand: 'dotnet test --filter Smoke' }))).toEqual(['AbbreviatedUnitTest']);
    expect(getAvailableCheckTypes(profile({ unitTestCommand: 'a', abbreviatedUnitTestCommand: 'b' }))).toEqual(['UnitTest', 'AbbreviatedUnitTest']);
    expect(getAvailableCheckTypes(profile({ buildCommand: 'dotnet build', abbreviatedUnitTestCommand: '' }))).not.toContain('AbbreviatedUnitTest');
  });

  it('counts the abbreviated command as a capability', () => {
    expect(countProfileCapabilities(profile({ unitTestCommand: 'a' }))).toBe(1);
    expect(countProfileCapabilities(profile({ unitTestCommand: 'a', abbreviatedUnitTestCommand: 'b' }))).toBe(2);
  });

  it('copies both unit-test commands when duplicating a profile', () => {
    const copy = buildWorkflowProfileDuplicatePayload(profile({ unitTestCommand: 'full', abbreviatedUnitTestCommand: 'quick' }));
    expect(copy.unitTestCommand).toBe('full');
    expect(copy.abbreviatedUnitTestCommand).toBe('quick');
    const empty = buildWorkflowProfileDuplicatePayload(profile());
    expect(empty.abbreviatedUnitTestCommand).toBeNull();
  });
});
