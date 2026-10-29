import { describe, expect, it } from 'vitest';
import { findLandingMode, getLandingModes } from './vesselForm';

const identity = (text: string) => text;

describe('getLandingModes', () => {
  const modes = getLandingModes(identity);

  it('lists the modes in the UI order with Merge and Push after Local Merge', () => {
    expect(modes.map((m) => m.value)).toEqual(['', 'LocalMerge', 'MergeAndPush', 'PullRequest', 'MergeQueue', 'None']);
  });

  it('says Local Merge does not push', () => {
    const local = findLandingMode(modes, 'LocalMerge');
    expect(local.label).toBe('Local Merge -- into your working directory, no push');
    expect(local.short).toBe('local, no push');
    expect(local.description).toContain('Nothing is pushed.');
    expect(local.description).not.toMatch(/pushes/);
  });

  it('offers Merge and Push with its own labels', () => {
    const push = findLandingMode(modes, 'MergeAndPush');
    expect(push.value).toBe('MergeAndPush');
    expect(push.label).toBe('Merge and Push -- verify and push to the remote');
    expect(push.short).toBe('local + push');
    expect(push.description).toBe("Integrates and verifies the mission in its dock, then pushes the verified commit to the remote target branch. A dirty working directory does not block publication.");
  });

  it('translates every label through t', () => {
    const tagged = getLandingModes((text) => `[${text}]`);
    expect(findLandingMode(tagged, 'MergeAndPush').label).toBe('[Merge and Push -- verify and push to the remote]');
    expect(findLandingMode(tagged, 'MergeAndPush').short).toBe('[local + push]');
  });
});
