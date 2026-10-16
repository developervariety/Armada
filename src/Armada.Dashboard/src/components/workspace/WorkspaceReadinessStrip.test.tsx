import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import WorkspaceReadinessStrip from './WorkspaceReadinessStrip';
import { translateTemplate } from '../../i18n/runtime';
import type { VesselReadinessIssue, VesselReadinessResult } from '../../types/models';

vi.mock('../../context/LocaleContext', () => {
  const locale = { t: (text: string, params?: Record<string, string | number>) => translateTemplate('en', text, null, params) };
  return { useLocale: () => locale };
});

const HARBOR_NOTE: VesselReadinessIssue = {
  code: 'working_directory_on_harbor',
  severity: 'Info',
  title: 'Checkout on Harbor laptop',
  message: 'Checks, Workspace, and readiness probes run in the checkout /Users/me/Code/app on Harbor laptop.',
  relatedValue: '/Users/me/Code/app',
};

function readiness(overrides: Partial<VesselReadinessResult> = {}): VesselReadinessResult {
  return {
    vesselId: 'vsl_1', hasWorkingDirectory: true, checkoutPath: '/Users/me/Code/app', harborId: 'hbr_1', harborName: 'laptop',
    checkoutErrorCode: null, hasRepositoryContext: true, workflowProfileId: 'wfp_1', workflowProfileName: 'App: .NET',
    workflowProfileScope: 'Vessel', requestedCheckType: null, requestedEnvironmentName: null,
    availableCheckTypes: ['Build', 'Test', 'Lint', 'Format'], currentBranch: 'main', hasUncommittedChanges: false,
    isDetachedHead: false, commitsAhead: 0, commitsBehind: 0, detectedToolchains: [], toolchainProbes: [],
    deploymentEnvironments: [], deploymentMetadata: null, setupChecklist: [], issues: [HARBOR_NOTE],
    setupChecklistSatisfiedCount: 0, setupChecklistTotalCount: 0, errorCount: 0, warningCount: 0, isReady: true,
    ...overrides,
  };
}

function renderStrip(value: VesselReadinessResult | null, loading = false) {
  return render(<MemoryRouter><WorkspaceReadinessStrip readiness={value} loading={loading} /></MemoryRouter>);
}

beforeEach(() => {
  localStorage.clear();
});

describe('WorkspaceReadinessStrip', () => {
  it('sums readiness up on one line, with the details closed', () => {
    renderStrip(readiness());
    const strip = screen.getByTestId('workspace-readiness');
    expect(within(strip).getByText('Ready')).toBeInTheDocument();
    expect(within(strip).getByText('App: .NET')).toBeInTheDocument();
    expect(within(strip).getByText('4 check type(s)')).toBeInTheDocument();
    expect(within(strip).getByText('on Harbor laptop at /Users/me/Code/app')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Readiness' })).toHaveAttribute('aria-expanded', 'false');
    // The redundant lines and the Harbor note stay out of the way.
    expect(screen.queryByText('Working directory available')).not.toBeInTheDocument();
    expect(screen.queryByText('Checkout on Harbor laptop')).not.toBeInTheDocument();
  });

  it('opens the details, with notes collapsed to their title, and remembers the choice', () => {
    const { unmount } = renderStrip(readiness());
    fireEvent.click(screen.getByRole('button', { name: 'Readiness' }));
    const note = screen.getByTestId('readiness-issue-working_directory_on_harbor');
    expect(note.tagName).toBe('DETAILS');
    expect(note).not.toHaveAttribute('open');
    expect(within(note).getByText('Checkout on Harbor laptop')).toBeInTheDocument();
    // Nothing the strip already shows is repeated.
    expect(screen.queryByText('Working directory available')).not.toBeInTheDocument();
    expect(screen.queryByText('Repository context available')).not.toBeInTheDocument();
    unmount();

    renderStrip(readiness());
    expect(screen.getByRole('button', { name: 'Readiness' })).toHaveAttribute('aria-expanded', 'true');
  });

  it('starts open when the vessel is blocked, and counts the issues', () => {
    renderStrip(readiness({
      errorCount: 1,
      hasRepositoryContext: false,
      issues: [{ code: 'repo_missing', severity: 'Error', title: 'Repository unavailable', message: 'No bare clone.', relatedValue: null }],
    }));
    expect(screen.getByText('Blocked')).toBeInTheDocument();
    expect(screen.getByText('1 issue(s)')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Readiness' })).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('Repository unavailable')).toBeInTheDocument();
    expect(screen.getByText('Repository context unavailable')).toBeInTheDocument();
  });

  it('says it is checking while loading', () => {
    renderStrip(null, true);
    expect(screen.getByText('Checking readiness...')).toBeInTheDocument();
  });
});
