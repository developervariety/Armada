import { buildVesselUpdatePayload } from './vesselUpdatePayload';

// A vessel as GET returns it, including fields the dashboard forms never edit.
const loaded = {
  id: 'vsl_example',
  tenantId: 'default',
  userId: 'default',
  name: 'Example',
  fleetId: 'flt_example',
  repoUrl: 'https://github.com/example/repo.git',
  defaultBranch: 'main',
  protectedPaths: ['**/CLAUDE.md'],
  siblingRepos: '[{"name":"Sibling"}]',
  defaultPlaybooks: '[{"playbookId":"pbk_example","deliveryMode":"InlineFullContent"}]',
  autoLandPredicate: '{"Enabled":true,"MaxFiles":5}',
  architectMaxMissionsPerVoyage: 4,
  hasGitHubTokenOverride: true,
  autoLandCalibrationLandedCount: 12,
  createdUtc: '2026-09-01T00:00:00Z',
  lastUpdateUtc: '2026-09-02T00:00:00Z',
  autoLandEnabled: true,
  autoLandMaxFiles: 5,
};

describe('buildVesselUpdatePayload', () => {
  it('keeps configuration the form does not edit', () => {
    const payload = buildVesselUpdatePayload(loaded, { name: 'Renamed' });

    expect(payload.name).toBe('Renamed');
    expect(payload.protectedPaths).toEqual(['**/CLAUDE.md']);
    expect(payload.siblingRepos).toBe('[{"name":"Sibling"}]');
    expect(payload.defaultPlaybooks).toBe('[{"playbookId":"pbk_example","deliveryMode":"InlineFullContent"}]');
    expect(payload.architectMaxMissionsPerVoyage).toBe(4);
  });

  it('sends the stored auto-land predicate as an object, not the stored string', () => {
    const payload = buildVesselUpdatePayload(loaded, {});

    expect(payload.autoLandPredicate).toEqual({ Enabled: true, MaxFiles: 5 });
  });

  it('lets the form replace or clear the auto-land predicate explicitly', () => {
    expect(buildVesselUpdatePayload(loaded, { autoLandPredicate: { enabled: false, maxFiles: 2 } }).autoLandPredicate)
      .toEqual({ enabled: false, maxFiles: 2 });
    const cleared = buildVesselUpdatePayload(loaded, { autoLandPredicate: null });
    expect('autoLandPredicate' in cleared).toBe(true);
    expect(cleared.autoLandPredicate).toBeNull();
  });

  it('drops read-only response fields and the retired flat auto-land fields', () => {
    const payload = buildVesselUpdatePayload(loaded, {});

    for (const key of ['id', 'tenantId', 'userId', 'hasGitHubTokenOverride', 'autoLandCalibrationLandedCount', 'createdUtc', 'lastUpdateUtc', 'autoLandEnabled', 'autoLandMaxFiles']) {
      expect(key in payload).toBe(false);
    }
  });

  it('omits an unparsable stored predicate rather than sending the raw string', () => {
    const payload = buildVesselUpdatePayload({ ...loaded, autoLandPredicate: '{not json' }, {});

    expect('autoLandPredicate' in payload).toBe(false);
  });
});
