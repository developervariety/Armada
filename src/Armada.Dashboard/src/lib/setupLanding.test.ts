import { describe, expect, it } from 'vitest';
import { SETUP_LANDING_MODES, setupLandingModeHint, setupLandingWorkingDirectoryError } from './setupLanding';

describe('setup wizard landing modes', () => {
  it('offers Merge and Push right after Local Merge', () => {
    expect(SETUP_LANDING_MODES.map((m) => m.value)).toEqual(['', 'None', 'LocalMerge', 'MergeAndPush', 'PullRequest', 'MergeQueue']);
    expect(SETUP_LANDING_MODES.find((m) => m.value === 'MergeAndPush')?.label).toBe('Merge and Push');
  });

  it('says Local Merge pushes nothing and Merge and Push needs an origin remote', () => {
    expect(setupLandingModeHint('LocalMerge')).toBe('Finished work is merged into the working directory. Nothing is pushed.');
    expect(setupLandingModeHint('MergeAndPush')).toBe('Finished work is integrated and verified in its dock, then pushed to the target branch. Your checkout is left untouched if it has changes.');
    expect(setupLandingModeHint('Unknown')).toBe(setupLandingModeHint(''));
  });

  it('requires a working directory only for Local Merge', () => {
    expect(setupLandingWorkingDirectoryError('LocalMerge', '')).toBe('Local Merge needs a working directory to merge into.');
    expect(setupLandingWorkingDirectoryError('MergeAndPush', '   ')).toBeNull();
    expect(setupLandingWorkingDirectoryError('MergeAndPush', '/work/svc')).toBeNull();
    expect(setupLandingWorkingDirectoryError('LocalMerge', '/work/svc')).toBeNull();
    for (const mode of ['', 'None', 'PullRequest', 'MergeQueue']) {
      expect(setupLandingWorkingDirectoryError(mode, '')).toBeNull();
    }
  });
});
