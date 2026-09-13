import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import LandingPreviewCard from './LandingPreviewCard';
import type { LandingPreviewResult } from '../../types/models';

vi.mock('../../context/LocaleContext', () => ({ useLocale: () => ({ t: (s: string) => s }) }));

function preview(overrides: Partial<LandingPreviewResult>): LandingPreviewResult {
  return {
    vesselId: 'vsl_example',
    missionId: null,
    sourceBranch: 'feature/work',
    targetBranch: 'main',
    branchCategory: 'Feature',
    targetBranchProtected: false,
    protectedBranchMatch: null,
    landingMode: 'LocalMerge',
    branchCleanupPolicy: null,
    requirePassingChecksToLand: true,
    requirePullRequestForProtectedBranches: false,
    requireMergeQueueForReleaseBranches: false,
    expectedLandingAction: null,
    hasPassingChecks: true,
    latestCheckRunId: null,
    latestCheckStatus: null,
    latestCheckSummary: null,
    isReadyToLand: true,
    issues: [],
    ...overrides,
  } as LandingPreviewResult;
}

const messages = { unavailableMessage: 'Not available.', noIssuesMessage: 'No issues predicted.' };

describe('LandingPreviewCard', () => {
  it('never presents a preview without errors as a landing verdict', () => {
    render(<LandingPreviewCard preview={preview({ isReadyToLand: true })} loading={false} meta="feature/work -> main" {...messages} />);

    expect(screen.getByText('No blocking preview issues')).toBeTruthy();
    expect(screen.queryByText('Ready To Land')).toBeNull();
  });

  it('states that its check evidence is not the Check gate for the landed commit', () => {
    render(<LandingPreviewCard preview={preview({})} loading={false} meta="feature/work -> main" {...messages} />);

    expect(screen.getByText(/not the Check gate for the landed commit/)).toBeTruthy();
    expect(screen.getByText('Advisory preview setting: passing checks required')).toBeTruthy();
  });

  it('lists preview issues with their severity, including a newer check that did not pass', () => {
    render(
      <LandingPreviewCard
        preview={preview({
          isReadyToLand: true,
          issues: [{ code: 'latest_check_not_passed', severity: 'Warning', title: 'Newest check did not pass', message: 'The newest structured check is Failed.' }],
        } as Partial<LandingPreviewResult>)}
        loading={false}
        meta="feature/work -> main"
        {...messages}
      />,
    );

    expect(screen.getByText('Newest check did not pass')).toBeTruthy();
    expect(screen.getByText('Warning')).toBeTruthy();
    expect(screen.queryByText('No issues predicted.')).toBeNull();
  });

  it('shows the error pill label, loading and unavailable states', () => {
    const { rerender } = render(<LandingPreviewCard preview={preview({ isReadyToLand: false })} loading={false} meta="m" {...messages} />);
    expect(screen.getByText('Preview issues')).toBeTruthy();

    rerender(<LandingPreviewCard preview={null} loading meta="m" {...messages} />);
    expect(screen.getByText('Calculating landing preview...')).toBeTruthy();

    rerender(<LandingPreviewCard preview={null} loading={false} meta="m" {...messages} />);
    expect(screen.getByText('Not available.')).toBeTruthy();
  });
});
