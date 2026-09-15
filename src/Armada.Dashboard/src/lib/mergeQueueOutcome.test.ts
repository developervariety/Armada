import { describe, expect, it } from 'vitest';
import { mergeDeleteOutcome } from './mergeQueueOutcome';

describe('mergeDeleteOutcome', () => {
  it.each(['Landed', 'Failed', 'Cancelled'])('a terminal %s entry is deleted', (status) => {
    expect(mergeDeleteOutcome(status)).toBe('deleted');
  });

  it.each(['Queued', 'Testing', 'Rebasing', 'Merging', 'Passed', 'Pushing', 'CreatingPR', 'PullRequestOpen'])(
    'an active %s entry is cancelled, not deleted',
    (status) => {
      expect(mergeDeleteOutcome(status)).toBe('cancelled');
    },
  );
});
