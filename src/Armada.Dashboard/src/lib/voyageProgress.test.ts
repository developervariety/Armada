import { describe, expect, it } from 'vitest';
import { summarizeVoyageProgress } from './voyageProgress';

describe('summarizeVoyageProgress', () => {
  it('counts landing failures as failed and cancelled missions as finished', () => {
    const summary = summarizeVoyageProgress([
      { status: 'Complete' },
      { status: 'Failed' },
      { status: 'LandingFailed' },
      { status: 'Cancelled' },
      { status: 'InProgress' },
    ]);
    expect(summary).toEqual({ total: 5, completed: 1, failed: 2, cancelled: 1, finished: 4, percent: 80 });
  });

  it('reports zero progress for a voyage with no missions', () => {
    expect(summarizeVoyageProgress([])).toEqual({ total: 0, completed: 0, failed: 0, cancelled: 0, finished: 0, percent: 0 });
  });
});
