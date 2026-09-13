import { describe, expect, it } from 'vitest';
import { LANDING_MODE_OPTIONS, landingModeHelp } from './landingModes';

describe('landing mode options', () => {
  it('offers the server landing modes plus the inherited default', () => {
    expect(LANDING_MODE_OPTIONS.map((option) => option.value)).toEqual(['', 'LocalMerge', 'PullRequest', 'MergeQueue', 'None']);
  });

  it('describes LocalMerge as a local landing that does not push', () => {
    const help = landingModeHelp('LocalMerge');
    expect(help).toContain('working checkout');
    expect(help).toContain('does not push');
  });

  it('describes PullRequest as complete only after the pull request merges', () => {
    expect(landingModeHelp('PullRequest')).toContain('merged');
  });

  it('describes the default as inherited, with a voyage landing mode taking priority', () => {
    const help = landingModeHelp('');
    expect(help).toContain('global');
    expect(help).toContain('voyage');
  });

  it('returns no help for an unknown value', () => {
    expect(landingModeHelp('Teleport')).toBe('');
  });
});
