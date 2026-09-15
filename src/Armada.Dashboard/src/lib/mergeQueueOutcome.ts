/** Merge entry statuses after which the entry no longer changes. */
export const TERMINAL_MERGE_STATUSES = ['Landed', 'Failed', 'Cancelled'] as const;

/** Every merge entry status the server can report, for the server-side status filter. */
export const MERGE_STATUSES = [
  'Queued', 'Testing', 'Rebasing', 'Merging', 'Passed', 'Pushing', 'CreatingPR', 'PullRequestOpen', 'Failed', 'Landed', 'Cancelled',
] as const;

/**
 * What `DELETE /api/v1/merge-queue/{id}` does to an entry. The route deletes a terminal entry and
 * cancels an active one, and answers 204 in both cases, so the outcome follows from the status.
 */
export function mergeDeleteOutcome(status: string | null | undefined): 'deleted' | 'cancelled' {
  return (TERMINAL_MERGE_STATUSES as readonly string[]).includes(status ?? '') ? 'deleted' : 'cancelled';
}
